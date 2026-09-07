using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Simulator;

public sealed record TransmitSelfTestResult(int SchemaVersion, bool Passed, bool LoopbackOnly,
    bool TransmitSimulated, bool HardwareContacted, TransmitState Transmit, long ElapsedMilliseconds);

/// <summary>Self-contained TX test. No endpoint argument or externally discovered radio is accepted.</summary>
public static class TransmitDiagnostics
{
    public static async Task<TransmitSelfTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            await using var simulator = G2Simulator.Open(new(BasePort: 0, SimulateTransmit: true), token);
            using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            peer.Client.ReceiveBufferSize = 262144;
            int port = simulator.BasePort;
            byte[] general = new byte[60]; general[37] = 8;
            foreach (var (field, offset) in new[] { (5, 1), (7, 2), (9, 3), (11, 1), (13, 4), (15, 5), (17, 11), (19, 2) })
                BinaryPrimitives.WriteUInt16BigEndian(general.AsSpan(field), (ushort)(port + offset));
            await Send(0, general);
            await SimulatorDiagnostics.WaitFor(() => simulator.State.Configured, token);
            byte[] rx = new byte[1444]; rx[4] = 2; // no DDC needed for the TX sink
            await Send(1, rx);
            await SimulatorDiagnostics.WaitFor(() => simulator.State.AcceptedControls >= 2, token);
            byte[] tx = new byte[60]; tx[4] = 1; tx[15] = 192; tx[16] = 24;
            await Send(2, tx);
            await SimulatorDiagnostics.WaitFor(() => simulator.State.Transmit.Configured, token);
            byte[] high = new byte[1444]; high[4] = 3; high[345] = 128;
            BinaryPrimitives.WriteUInt32BigEndian(high.AsSpan(329), (uint)(14_200_000.0 * 4294967296.0 / 122880000.0));
            await Send(3, high);
            await SimulatorDiagnostics.WaitFor(() => simulator.State.Transmit.Ptt, token);

            // 1 kHz complex tone at 192 kHz: 100 frames = exactly 125 cycles.
            // Bounded handshakes prevent overwhelming a slow CI scheduler; not a clock benchmark.
            byte[] iq = new byte[1444];
            for (int packet = 0; packet < 100; ++packet)
            {
                if (packet % 10 == 0) await Send(3, high);
                BinaryPrimitives.WriteUInt32BigEndian(iq, (uint)packet);
                for (int sample = 0; sample < 240; ++sample)
                {
                    double phase = 2 * Math.PI * (packet * 240 + sample) / 192;
                    P2Device.Write24(iq.AsSpan(4 + sample * 6), 0.25 * Math.Cos(phase));
                    P2Device.Write24(iq.AsSpan(7 + sample * 6), 0.25 * Math.Sin(phase));
                }
                await Send(5, iq);
                await SimulatorDiagnostics.WaitFor(() => simulator.State.Transmit.Packets >= packet + 1, token);
            }
            var measured = simulator.State.Transmit;
            Require(measured.Packets == 100 && measured.Samples == 24000 && measured.Bursts == 1 &&
                measured.MissingPackets == 0 && measured.OutOfOrderPackets == 0 && measured.FullScaleComponents == 0 &&
                Math.Abs(measured.RmsMagnitude - 0.25) < 0.000001 && Math.Abs(measured.PeakMagnitude - 0.25) < 0.000001 &&
                Math.Abs(measured.MeanI) < 0.000001 && Math.Abs(measured.MeanQ) < 0.000001 &&
                Math.Abs(measured.FrequencyHz - 14_200_000) < 0.03 && measured.Drive == 128, "TX sample metrics");

            // Even while software PTT is asserted, physical input/telemetry bits remain zero.
            UdpReceiveResult status;
            do { status = await peer.ReceiveAsync(token); } while (status.RemoteEndPoint.Port != port + 1);
            Require(status.Buffer.Length == 60 && status.Buffer.AsSpan(4).IndexOfAnyExcept((byte)0) < 0, "physical status is not a PTT echo");
            high[4] = 1; await Send(3, high);
            await SimulatorDiagnostics.WaitFor(() => !simulator.State.Transmit.Ptt, token);
            BinaryPrimitives.WriteUInt32BigEndian(iq, 100);
            await Send(5, iq);
            await SimulatorDiagnostics.WaitFor(() => simulator.State.Transmit.UnkeyedPackets == 1, token);
            high[4] = 0; await Send(3, high);
            await SimulatorDiagnostics.WaitFor(() => !simulator.State.Running, token);
            var final = simulator.State;
            Require(!final.Transmit.Ptt && final.Transmit.Packets == 100 && final.UnsafeRequests == 0 &&
                final.RejectedPackets == 0 && final.SocketErrors == 0, "PTT off and stopped");
            return new(1, true, true, true, false, final.Transmit, clock.ElapsedMilliseconds);

            async Task Send(int offset, byte[] bytes) =>
                await peer.SendAsync(bytes, new IPEndPoint(IPAddress.Loopback, port + offset), token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Simulator TX self-test did not complete within five seconds."); }
    }
    private static void Require(bool condition, string check)
    { if (!condition) throw new InvalidOperationException($"Simulator TX check failed: {check}."); }
}
