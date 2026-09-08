using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

internal static class ReceiveControlsCli
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default, Func<string, CancellationToken, Task<ReceiveControlsResult>>? runner = null)
    {
        const string help = "Usage: Thetis.Headless receive-controls-selftest --native-dir ABSOLUTE_PATH\nTests USB/LSB and receive filters using an owned loopback simulator. No hardware, TX or audio devices.";
        if (args is [_, "--help"]) { output.WriteLine(help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? ReceiveControlsSelfTest.RunAsync)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("Receive controls test cancelled; owners disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load compatible native receive controls: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or SocketException)
        { error.WriteLine($"Receive controls test failed: {ex.Message}"); return 4; }
    }
}

public sealed record ReceiveControlCheck(string Name, ReceiveDemodulationState Controls, double Rms,
    double ToneHz, bool ExpectedPassband, bool Passed);
public sealed record ReceiveControlsResult(int SchemaVersion, bool Passed, bool LoopbackOnly, bool TransmitAllowed,
    long ElapsedMilliseconds, IReadOnlyList<ReceiveControlCheck> Checks, P2ReceiveState Native, SimulatorState Simulator);

/// <summary>Fixed signal fixtures, not a configurable hardware receive command.</summary>
public static class ReceiveControlsSelfTest
{
    public static async Task<ReceiveControlsResult> RunAsync(string nativeDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var checks = new List<ReceiveControlCheck>();
        await using var simulator = G2Simulator.Open(new(BasePort: 0), token);
        P2ReceiveState? final = null;
        foreach (var initial in new[] { new ReceiveDemodulation(), new ReceiveDemodulation(ReceiveMode.Lsb, 500, 2500) })
        {
            int center = initial.Mode == ReceiveMode.Usb ? 14_199_000 : 14_201_000;
            using var session = P2ReceiveSession.Open(nativeDirectory, new(simulator.BasePort, FrequencyHz: center, Demodulation: initial), token);
            if (session.Demodulation != new ReceiveDemodulationState(initial, 1))
                throw new InvalidOperationException("Startup controls were not applied before receive.");
            Check("initial/reconnect passband", true);
            var opposite = initial with { Mode = initial.Mode == ReceiveMode.Usb ? ReceiveMode.Lsb : ReceiveMode.Usb };
            session.ConfigureDemodulation(opposite); Check("opposite sideband rejection", false);
            session.ConfigureDemodulation(initial with { HighCutHz = 700 }); Check("high-cut rejection", false);
            session.ConfigureDemodulation(initial with { LowCutHz = 1500 }); Check("low-cut rejection", false);
            session.ConfigureDemodulation(initial with { LowCutHz = 700, HighCutHz = 1400 }); Check("narrow passband", true);
            session.ConfigureDemodulation(initial); Check("restored passband", true);
            var before = session.Demodulation;
            session.ConfigureDemodulation(initial);
            if (session.Demodulation != before) throw new InvalidOperationException("Identical controls changed generation.");
            // Demodulation controls must not alter pre-filter RF spectrum or its tune generation.
            ReceiveSelfTest.MeasureSpectrum(session, center, 14_200_000, 1, token);
            final = session.State;
            RequireClean(final);
            int port = final.LocalPort;
            session.Dispose();
            var stop = Stopwatch.StartNew();
            while (simulator.State.Running)
            {
                if (stop.Elapsed.TotalSeconds >= 2) throw new TimeoutException("No native STOP after controls campaign.");
                await Task.Delay(5, token);
            }
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));

            void Check(string name, bool passband)
            {
                var controls = session.Demodulation;
                var measured = MeasureSettled(session, token);
                bool passed = passband ? Math.Abs(measured.Rms - 0.25 / Math.Sqrt(2)) <= 0.005 && Math.Abs(measured.ToneHz - 1000) <= 20
                    : measured.Rms < 0.00056; // approximately 50 dB below the expected 0.1768 RMS passband
                checks.Add(new(name, controls, measured.Rms, measured.ToneHz, passband, passed));
                RequireClean(session.State);
                if (!passed) throw new InvalidOperationException($"{name}: {controls}, RMS={measured.Rms:G6}, tone={measured.ToneHz:F2} Hz.");
            }
        }
        var peer = simulator.State;
        if (peer.Running || peer.UnsafeRequests != 0 || peer.SocketErrors != 0 || peer.WatchdogStops != 0 || peer.Transmit.Ptt || peer.Transmit.Packets != 0)
            throw new InvalidOperationException("Receive controls safety/shutdown check failed.");
        return new(1, true, true, false, clock.ElapsedMilliseconds, checks, final!, peer);
    }

    public static ReceiveMeasurement MeasureSettled(P2ReceiveSession session, CancellationToken token = default)
    {
        // Drain one audio second to exclude queued I/O and FIR transition history.
        // Count consumed samples, not a sleep, so slow simulated hosts cannot fake settling.
        double[] audio = new double[4096];
        int skip = 48000, frames = 0, crossings = 0;
        double energy = 0, previous = 0;
        var timer = Stopwatch.StartNew();
        while (frames < 8192)
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("Receive control measurement stalled.");
            int count = session.ReadAudio(audio);
            for (int i = 0; i < count; ++i)
            {
                double left = audio[2 * i], right = audio[2 * i + 1];
                if (!double.IsFinite(left) || !double.IsFinite(right)) throw new InvalidDataException("Non-finite control-transition audio.");
                if (skip > 0) { --skip; continue; }
                if (frames > 0 && previous <= 0 && left > 0) ++crossings;
                previous = left; energy += left * left; ++frames;
            }
            if (count == 0) Thread.Sleep(5);
        }
        return new(Math.Sqrt(energy / frames), crossings * 48000.0 / (frames - 1), frames);
    }

    private static void RequireClean(P2ReceiveState state)
    {
        if (state.SocketErrors != 0 || state.DspErrors != 0 || state.InputOverruns != 0 || state.MissingPackets != 0 || state.AudioDropped != 0)
            throw new InvalidOperationException($"Errors during control switching: {state}");
    }
}
