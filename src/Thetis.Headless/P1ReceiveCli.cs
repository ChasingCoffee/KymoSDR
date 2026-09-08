using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

internal static class P1ReceiveCli
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default, Func<string, CancellationToken, Task<P1ReceiveResult>>? runner = null)
    {
        const string help = "Usage: Thetis.Headless p1-receive-selftest --native-dir ABSOLUTE_PATH\nOwned loopback P1 fixture, one receiver at 48 kHz. No hardware, transmit or audio device.";
        if (args is [_, "--help"]) { output.WriteLine(help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? P1ReceiveSelfTest.RunAsync)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("P1 receive test cancelled; owners disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load native P1 receiver: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or SocketException)
        { error.WriteLine($"P1 receive test failed: {ex.Message}"); return 4; }
    }
}

public sealed record P1ReceiveResult(int SchemaVersion, bool Passed, bool LoopbackOnly, bool TransmitAllowed,
    long ElapsedMilliseconds, IReadOnlyList<ReceiveControlCheck> Checks, IReadOnlyList<ReceiveSpectrumMeasurement> Spectra,
    IReadOnlyList<ReceiveState> Sessions, P1SimulatorState Simulator);

public static class P1ReceiveSelfTest
{
    public static async Task<P1ReceiveResult> RunAsync(string directory, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        var checks = new List<ReceiveControlCheck>(); var spectra = new List<ReceiveSpectrumMeasurement>();
        var states = new List<ReceiveState>();
        await using var simulator = P1Simulator.Open(token: token);
        foreach (var mode in new[] { ReceiveMode.Usb, ReceiveMode.Lsb })
        {
            int center = mode == ReceiveMode.Usb ? 14_199_000 : 14_201_000;
            var initial = new ReceiveDemodulation(mode);
            using var session = P1ReceiveSession.Open(directory, new(simulator.Port, center, Demodulation: initial), token);
            if (session.Demodulation != new ReceiveDemodulationState(initial, 1)) throw new InvalidOperationException("P1 startup controls not applied.");
            Check(initial, true, 1000);
            spectra.Add(ReceiveSelfTest.MeasureSpectrum(session, center, 14_200_000, 1, token));
            Check(initial with { Mode = mode == ReceiveMode.Usb ? ReceiveMode.Lsb : ReceiveMode.Usb }, false, 1000);
            Check(initial with { HighCutHz = 700 }, false, 1000);
            Check(initial with { LowCutHz = 1500 }, false, 1000);
            Check(initial with { LowCutHz = 700, HighCutHz = 1400 }, true, 1000);
            Check(initial, true, 1000);
            int tuned = center + (mode == ReceiveMode.Usb ? -500 : 500);
            session.Tune(tuned);
            Check(initial, true, 1500);
            spectra.Add(ReceiveSelfTest.MeasureSpectrum(session, tuned, 14_200_000, 2, token));
            var state = session.State; RequireClean(state); states.Add(state);
            session.Dispose();
            await WaitUntil(() => !simulator.State.Running, token);
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, state.LocalPort));

            void Check(ReceiveDemodulation settings, bool pass, int hz)
            {
                session.ConfigureDemodulation(settings);
                var measured = ReceiveControlsSelfTest.MeasureSettled(session, token);
                bool ok = pass ? Math.Abs(measured.Rms - .25 / Math.Sqrt(2)) < .005 && Math.Abs(measured.ToneHz - hz) <= 20 : measured.Rms < .00056;
                checks.Add(new($"{mode}: {settings.LowCutHz}..{settings.HighCutHz}, {hz} Hz", session.Demodulation, measured.Rms, measured.ToneHz, pass, ok));
                RequireClean(session.State);
                if (!ok) throw new InvalidOperationException($"P1 signal check failed: {checks[^1]}");
            }
        }
        var peer = simulator.State;
        if (peer.Running || peer.Starts != 2 || peer.Stops != 2 || peer.UnsafeRequests != 0 || peer.MalformedRequests != 0 || peer.SocketErrors != 0 || peer.WatchdogStops != 0)
            throw new InvalidOperationException($"P1 fixture safety/STOP check failed: {peer}");
        return new(1, true, true, false, clock.ElapsedMilliseconds, checks, spectra, states, peer);
    }
    public static void RequireClean(ReceiveState s)
    {
        if (s.SocketErrors != 0 || s.DspErrors != 0 || s.InputOverruns != 0 || s.AudioDropped != 0 || s.MissingPackets != 0 ||
            s.MalformedPackets != 0 || s.ForeignPackets != 0 || s.LatePackets != 0 || s.IqSamples != s.IqPackets * 126 ||
            s.MicPacketsDiscarded != s.IqPackets || s.StatusPackets != s.IqPackets)
            throw new InvalidOperationException($"P1 receive diagnostics failed: {s}");
    }
    public static async Task WaitUntil(Func<bool> condition, CancellationToken token = default, int seconds = 2)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        { if (clock.Elapsed.TotalSeconds > seconds) throw new TimeoutException("P1 fixture condition timed out."); await Task.Delay(5, token); }
    }
}
