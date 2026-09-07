using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Simulator;

public sealed record SimulatorSelfTestResult(int SchemaVersion, bool Passed, bool LoopbackOnly,
    int IqPackets, int MicPackets, int StatusPackets, long ElapsedMilliseconds);

public static class SimulatorDiagnostics
{
    public static async Task<SimulatorSelfTestResult> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        try
        {
            await using var simulator = G2Simulator.Open(new(BasePort: 0), token);
            using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            int port = simulator.BasePort;
            byte[] discovery = new byte[60]; discovery[4] = 2;
            await Send(0, discovery);
            var reply = await peer.ReceiveAsync(token);
            Require(reply.RemoteEndPoint.Port == port && reply.Buffer.Length == 60 && reply.Buffer[4] == 2 && reply.Buffer[11] == 10, "discovery");
            byte[] general = new byte[60]; general[37] = 8;
            foreach (var (field, offset) in new[] { (5, 1), (7, 2), (9, 3), (11, 1), (13, 4), (15, 5), (17, 11), (19, 2) })
                BinaryPrimitives.WriteUInt16BigEndian(general.AsSpan(field), (ushort)(port + offset));
            await Send(0, general);
            await WaitFor(() => simulator.State.Configured, token);
            byte[] rx = new byte[1444]; rx[4] = 2; rx[7] = 4; // DDC2, a G2 RX routing case
            rx[31] = 48; rx[34] = 24;
            await Send(1, rx);
            await WaitFor(() => simulator.State.Receivers[2].Enabled, token);
            byte[] high = new byte[1444]; high[4] = 1;
            uint phase = (uint)(14_199_000.0 * 4_294_967_296.0 / 122_880_000.0);
            BinaryPrimitives.WriteUInt32BigEndian(high.AsSpan(17), phase);
            await Send(3, high);
            int iq = 0, mic = 0, status = 0;
            double heartbeat = 0;
            while (iq < 100 || mic == 0 || status == 0)
            {
                if (clock.Elapsed.TotalSeconds >= heartbeat)
                { await Send(3, high); heartbeat = clock.Elapsed.TotalSeconds + 0.1; }
                var received = await peer.ReceiveAsync(token);
                var data = received.Buffer;
                switch (received.RemoteEndPoint.Port - port)
                {
                    case 13:
                        Require(data.Length == 1444 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(12)) == 24 &&
                            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(14)) == 238, "I/Q framing");
                        Require(BinaryPrimitives.ReadUInt32BigEndian(data) == (uint)iq, "I/Q sequence");
                        ++iq; break;
                    case 2:
                        Require(data.Length == 132 && data.AsSpan(4).IndexOfAnyExcept((byte)0) < 0, "microphone silence");
                        ++mic; break;
                    case 1:
                        Require(data.Length == 60 && data.AsSpan(4).IndexOfAnyExcept((byte)0) < 0, "inactive status");
                        ++status; break;
                    default: throw new InvalidOperationException("Unexpected simulator stream source port.");
                }
            }
            high[4] = 0; await Send(3, high);
            await WaitFor(() => !simulator.State.Running, token);
            var final = simulator.State;
            Require(final.Starts == 1 && final.UnsafeRequests == 0 && final.RejectedPackets == 0 && final.SocketErrors == 0, "receive-only lifecycle");
            return new(1, true, true, iq, mic, status, clock.ElapsedMilliseconds);

            async Task Send(int offset, byte[] data) =>
                await peer.SendAsync(data, new IPEndPoint(IPAddress.Loopback, port + offset), token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Simulator self-test did not complete within five seconds."); }
    }
    private static void Require(bool condition, string check)
    { if (!condition) throw new InvalidOperationException($"Simulator check failed: {check}."); }
    internal static async Task WaitFor(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) { token.ThrowIfCancellationRequested(); await Task.Delay(5, token); }
    }
}
