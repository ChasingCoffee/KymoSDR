using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

internal static class ReceiveCli
{
    private const string Help = "Usage: Thetis.Headless receive-selftest --native-dir ABSOLUTE_PATH\nStarts its own loopback simulator, feeds native P2/ChannelMaster/WDSP and measures RX audio. No hardware, TX or audio devices.";
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default,
        Func<string, CancellationToken, Task<ReceiveSelfTestResult>>? runner = null)
    {
        if (args is [_, "--help"]) { output.WriteLine(Help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? ReceiveSelfTest.RunAsync)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 0;
        }
        catch (OperationCanceledException) { error.WriteLine("Receive self-test cancelled; native receiver and simulator disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load compatible native receive code: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or SocketException)
        { error.WriteLine($"Receive self-test failed: {ex.Message}"); return 4; }
    }
}

public sealed record ReceiveMeasurement(double Rms, double ToneHz, long Frames);
public sealed record ReceiveSelfTestResult(int SchemaVersion, bool Passed, bool LoopbackOnly, bool TransmitAllowed,
    ReceiveMeasurement BeforeTuning, ReceiveMeasurement AfterTuning, P2ReceiveState Native, SimulatorState Simulator,
    long ElapsedMilliseconds);

public static class ReceiveSelfTest
{
    public static async Task<ReceiveSelfTestResult> RunAsync(string nativeDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var clock = Stopwatch.StartNew();
        await using var simulator = G2Simulator.Open(new(BasePort: 0), token);
        using var session = P2ReceiveSession.Open(nativeDirectory, new(simulator.BasePort), token);
        var first = Measure(session, 1000, token);
        session.Tune(14_198_500);
        var second = Measure(session, 1500, token);
        var native = session.State;
        session.Dispose(); // stop/join native producer and CM consumers before simulator disposal
        var stopWait = Stopwatch.StartNew();
        while (simulator.State.Running)
        {
            if (stopWait.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Simulator did not observe native STOP.");
            await Task.Delay(10, token);
        }
        var peer = simulator.State;
        if (peer.UnsafeRequests != 0 || peer.Transmit.Ptt || peer.Transmit.Packets != 0 ||
            native.SocketErrors != 0 || native.DspErrors != 0 || native.InputOverruns != 0 ||
            native.MicPacketsDiscarded == 0 || native.StatusPackets == 0)
            throw new InvalidOperationException("Receive safety, routing or bounded-buffer check failed.");
        return new(1, true, true, false, first, second, native, peer, clock.ElapsedMilliseconds);
    }
    public static ReceiveMeasurement Measure(P2ReceiveSession session, double expectedHz, CancellationToken token = default)
    {
        // Drain during settling so the bounded pull queue cannot silently mask a stalled reader.
        var deadline = Stopwatch.StartNew();
        double[] buffer = new double[4096];
        long skipped = 0, frames = 0; int crossings = 0; double previous = 0, energy = 0;
        while (frames < 8192)
        {
            token.ThrowIfCancellationRequested();
            if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Native receive audio did not advance.");
            var state = session.State;
            if (state.SocketErrors != 0 || state.DspErrors != 0) throw new IOException("Native packet/DSP worker reported an error.");
            int count = session.ReadAudio(buffer);
            for (int i = 0; i < count; ++i)
            {
                if (skipped++ < 48000) continue; // at least one second of audio after start/tune
                double sample = buffer[2 * i];
                if (!double.IsFinite(sample)) throw new InvalidDataException("Non-finite demodulated audio.");
                if (frames > 0 && previous <= 0 && sample > 0) ++crossings;
                previous = sample; energy += sample * sample; ++frames;
            }
            if (count == 0) Thread.Sleep(5);
        }
        var result = new ReceiveMeasurement(Math.Sqrt(energy / frames), crossings * 48000.0 / (frames - 1), frames);
        if (result.Rms < 0.001 || result.Rms > 1 || Math.Abs(result.ToneHz - expectedHz) > 20)
            throw new InvalidOperationException($"Unexpected WDSP audio: RMS={result.Rms:G6}, tone={result.ToneHz:F2} Hz; expected {expectedHz} Hz.");
        return result;
    }
}
