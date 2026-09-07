using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

internal static class ReceiveCli
{
    private const string Help = "Usage: Thetis.Headless receive-selftest --native-dir ABSOLUTE_PATH\nStarts its own loopback simulator, feeds native P2/ChannelMaster/WDSP and measures RX audio and spectrum. No hardware, TX or audio devices.";
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
public sealed record ReceiveSpectrumMeasurement(long Sequence, long TuningGeneration, int RequestedCenterFrequencyHz,
    double PeakFrequencyHz, double PeakOffsetHz, double PeakDb, double BinWidthHz, long PublishedMonotonicMilliseconds,
    long CoalescedFrames);
public sealed record ReceiveSelfTestResult(int SchemaVersion, bool Passed, bool LoopbackOnly, bool TransmitAllowed,
    ReceiveMeasurement BeforeTuning, ReceiveMeasurement AfterTuning,
    ReceiveSpectrumMeasurement SpectrumBeforeTuning, ReceiveSpectrumMeasurement SpectrumAfterTuning,
    P2ReceiveState Native, SimulatorState Simulator,
    long ElapsedMilliseconds);

public static class ReceiveSelfTest
{
    public static async Task<ReceiveSelfTestResult> RunAsync(string nativeDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var clock = Stopwatch.StartNew();
        await using var simulator = G2Simulator.Open(new(BasePort: 0), token);
        using var session = P2ReceiveSession.Open(nativeDirectory, new(simulator.BasePort), token);
        var first = Measure(session, 1000, token);
        var firstSpectrum = MeasureSpectrum(session, 14_199_000, 14_200_000, 1, token);
        session.Tune(14_198_500);
        var second = Measure(session, 1500, token);
        var secondSpectrum = MeasureSpectrum(session, 14_198_500, 14_200_000, 2, token);
        if (secondSpectrum.Sequence <= firstSpectrum.Sequence ||
            secondSpectrum.PublishedMonotonicMilliseconds <= firstSpectrum.PublishedMonotonicMilliseconds)
            throw new InvalidOperationException("Spectrum sequence/time did not advance across tuning.");
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
        return new(2, true, true, false, first, second, firstSpectrum, secondSpectrum, native, peer, clock.ElapsedMilliseconds);
    }
    public static ReceiveSpectrumMeasurement MeasureSpectrum(P2ReceiveSession session, int centerHz, double rfHz,
        long generation, CancellationToken token = default)
    {
        var deadline = Stopwatch.StartNew();
        ReceiveSpectrumFrame? previous = null;
        double[] audio = new double[4096];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Native receive spectrum did not advance.");
            var state = session.State;
            if (state.SocketErrors != 0 || state.DspErrors != 0) throw new IOException("Native packet/DSP worker reported an error.");
            session.ReadAudio(audio);
            var frame = session.ReadSpectrum();
            if (frame is null) { Thread.Sleep(5); continue; }
            if (frame.TuningGeneration != generation || frame.RequestedCenterFrequencyHz != centerHz)
                throw new InvalidOperationException("Spectrum belongs to the wrong tuning generation.");
            var levels = frame.LevelsDb.Span;
            int peak = 0;
            for (int i = 0; i < levels.Length; ++i)
            {
                if (!float.IsFinite(levels[i])) throw new InvalidDataException("Non-finite spectrum pixel.");
                if (levels[i] > levels[peak]) peak = i;
            }
            double frequency = frame.FrequencyAt(peak);
            // The simulator's unit-complex amplitude is 0.25. Hann scalloping
            // permits up to ~1.5 dB below the coherent-bin value, not RF dBm.
            if (Math.Abs(frequency - rfHz) > frame.BinWidthHz || Math.Abs(levels[peak] - 20 * Math.Log10(0.25)) > 1.6)
                throw new InvalidOperationException($"Unexpected spectrum peak: {frequency:F2} Hz, {levels[peak]:F2} dB.");
            if (previous is not null)
            {
                if (frame.Sequence <= previous.Sequence || frame.PublishedMonotonicMilliseconds <= previous.PublishedMonotonicMilliseconds)
                    throw new InvalidOperationException("Spectrum returned a duplicate or non-advancing frame.");
                return new(frame.Sequence, frame.TuningGeneration, frame.RequestedCenterFrequencyHz, frequency,
                    frequency - centerHz, levels[peak], frame.BinWidthHz, frame.PublishedMonotonicMilliseconds, frame.CoalescedFrames);
            }
            previous = frame;
        }
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
