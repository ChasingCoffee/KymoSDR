using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

public sealed record ReceiveSoakOptions(int DurationSeconds = 60, int Reconnects = 2)
{
    internal void Validate()
    {
        if (DurationSeconds is < 10 or > 7200) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (Reconnects is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(Reconnects));
    }
}
public sealed record ReceiveSoakProgress(string Phase, double ElapsedSeconds, long IqPackets, long AudioFrames,
    long SpectrumFrames, long WorkingSetBytes);
public sealed record ReceiveSoakResources(long Samples, long InitialWorkingSetBytes, long FinalWorkingSetBytes,
    long PeakWorkingSetBytes, long WorkingSetGrowthBytes, long? InitialPrivateBytes, long? FinalPrivateBytes,
    long InitialManagedBytes, long FinalManagedBytes, long AllocatedManagedBytes, double CpuSeconds,
    double AverageCpuPercentOfOneCore, int InitialThreads, int FinalThreads);
public sealed record ReceiveSoakPhase(string Name, bool Passed, string? Failure, double ObservedSeconds,
    int Retunes, long AudioFramesRead, long SpectrumFramesRead, long SpectrumFramesProduced, long SpectrumFramesCoalesced,
    double SpectrumFramesPerSecond, double MaxSpectrumReadGapMilliseconds, int AudioSignalChecks,
    int SpectrumSignalChecks, ReceiveState? Native, SimulatorState? Simulator,
    ReceiveSoakResources? Resources, bool NativeDisposed, bool StopObserved, bool PortRebound,
    bool ExpectedPeerFailureObserved);
public sealed record ReceiveSoakResult(int SchemaVersion, bool Passed, bool Cancelled, string? Failure,
    bool LoopbackOnly, bool TransmitAllowed, int RequestedSteadySeconds, int ReconnectsCompleted,
    long ElapsedMilliseconds, IReadOnlyList<ReceiveSoakPhase> Phases);

/// <summary>Bounded headless endurance and fault campaign. All peers are owned IPv4-loopback simulators.</summary>
public static class ReceiveSoak
{
    public static async Task<ReceiveSoakResult> RunAsync(string nativeDirectory, ReceiveSoakOptions options,
        Action<ReceiveSoakProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(); token.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var phases = new List<ReceiveSoakPhase>();
        string? failure = null; bool cancelled = false; int reconnects = 0, reconnectPort = 0;
        try
        {
            await Phase("steady", options.DurationSeconds, retune: true);
            await Phase("packet-loss", 3, loss: true);
            await Phase("slow-reader", 4, slow: true);
            await Phase("peer-disappearance", 2, disconnect: true);
            for (int i = 0; i < options.Reconnects; ++i)
            { await Phase($"reconnect-{i + 1}", 2, basePort: reconnectPort); ++reconnects; }
        }
        catch (OperationCanceledException) { cancelled = true; failure = "Cancelled; partial measurements retained."; }
        catch (Exception ex) when (IsMeasurementFailure(ex)) { failure = ex.Message; }
        return new(1, failure is null, cancelled, failure, true, false, options.DurationSeconds, reconnects,
            clock.ElapsedMilliseconds, phases);

        async Task Phase(string name, int seconds, bool retune = false, bool loss = false, bool slow = false, bool disconnect = false, int basePort = 0)
        {
            token.ThrowIfCancellationRequested();
            G2Simulator? simulator = null; P2ReceiveSession? session = null;
            ReceiveState? native = null; SimulatorState? peer = null;
            ReceiveSoakObservation? observation = null;
            string? phaseFailure = null;
            bool disposed = false, stopped = false, rebound = false, peerGone = false, peerFailure = false;
            try
            {
                simulator = G2Simulator.Open(new(BasePort: basePort, DropEvery: loss ? 7 : 0));
                if (disconnect) reconnectPort = simulator.BasePort;
                session = P2ReceiveSession.Open(nativeDirectory, new(simulator.BasePort), token);
                // Warm the steady session before its timed resource baseline. The
                // existing measurements drain audio and verify both signal paths.
                if (retune)
                {
                    ReceiveSelfTest.Measure(session, 1000, token);
                    ReceiveSelfTest.MeasureSpectrum(session, 14_199_000, 14_200_000, 1, token);
                }
                observation = new();
                await ObserveAsync(name, seconds, session, simulator, observation, retune, loss, slow, progress, token);
                if (disconnect)
                {
                    await simulator.DisposeAsync(); peerGone = true;
                    await WaitUntil(() => session.State.SocketErrors > 0, 6000, token);
                    // Error accounting may precede the worker's final STOP. Allow
                    // it to exit before checking that heartbeats/tuning stopped.
                    await Task.Delay(250, token);
                    long commands = session.State.CommandsSent;
                    await Task.Delay(250, token);
                    Require(session.State.CommandsSent == commands, "Heartbeats continued after peer failure.");
                    bool rejected = false;
                    try { session.Tune(14_198_500); }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "Tuning remained available after peer failure.");
                    peerFailure = true;
                }
            }
            catch (Exception ex) { phaseFailure = ex.Message; throw; }
            finally
            {
                // Ignore operation cancellation during bounded cleanup; retain
                // counters before native disposal clears them. Always dispose the
                // simulator even when native/status/STOP validation fails.
                try
                {
                    if (session is not null)
                    {
                        try { native = session.State; }
                        finally { session.Dispose(); disposed = true; }
                        if (!peerGone && simulator is not null)
                        {
                            await WaitUntil(() => !simulator.State.Running, 2000, CancellationToken.None);
                            Require(simulator.State.WatchdogStops == 0, "Peer stopped by watchdog, not native STOP.");
                            stopped = true;
                        }
                        if (native is not null)
                        {
                            using var check = new UdpClient(new IPEndPoint(IPAddress.Loopback, native.LocalPort));
                            rebound = true;
                        }
                    }
                }
                catch (Exception ex) when (IsMeasurementFailure(ex)) { phaseFailure = $"{phaseFailure} Cleanup: {ex.Message}".Trim(); }
                finally
                {
                    if (simulator is not null)
                    {
                        try { await simulator.DisposeAsync(); }
                        catch (Exception ex) when (IsMeasurementFailure(ex)) { phaseFailure = $"{phaseFailure} Simulator cleanup: {ex.Message}".Trim(); }
                        finally { peer = simulator.State; }
                    }
                    phases.Add(new(name, phaseFailure is null && disposed && rebound && (stopped || peerFailure), phaseFailure,
                        observation?.ElapsedSeconds ?? 0, observation?.Retunes ?? 0, observation?.AudioFrames ?? 0,
                        observation?.SpectrumFrames ?? 0, observation?.ProducedSpectrumFrames ?? 0, observation?.CoalescedFrames ?? 0,
                        observation?.SpectrumCadence ?? 0, observation?.MaxSpectrumGapMs ?? 0,
                        observation?.AudioChecks ?? 0, observation?.SpectrumChecks ?? 0, native, peer,
                        observation?.Resources, disposed, stopped, rebound, peerFailure));
                }
            }
            Require(phases[^1].Passed, phases[^1].Failure ?? $"Incomplete cleanup in {name}.");
        }
    }

    private static async Task ObserveAsync(string name, int seconds, P2ReceiveSession session, G2Simulator simulator,
        ReceiveSoakObservation result, bool retune, bool loss, bool slow, Action<ReceiveSoakProgress>? progress, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        using var resources = new ReceiveSoakResourceSampler();
        double[] audio = new double[32768];
        var signal = new ReceiveSoakSignal();
        int center = 14_199_000;
        long generation = 1, lastIq = 0, lastAudio = 0, firstSpectrum = 0, lastSpectrum = 0, lastPublication = 0, firstCoalesced = 0;
        double iqAt = 0, audioAt = 0, spectrumAt = 0, tunedAt = 0, nextTune = Math.Min(15, seconds / 2.0), nextProgress = 0, nextResource = 0;
        bool held = false;
        var initial = session.State;
        try
        {
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                token.ThrowIfCancellationRequested(); double now = clock.Elapsed.TotalSeconds;
                if (retune && now >= nextTune)
                {
                    center = center == 14_199_000 ? 14_198_500 : 14_199_000;
                    session.Tune(center); ++generation; ++result.Retunes;
                    tunedAt = now; nextTune = now + Math.Min(15, seconds / 2.0); signal.Reset();
                }
                var state = session.State;
                ValidateState(state, loss, slow);
                var peer = simulator.State;
                Require(peer.UnsafeRequests == 0 && peer.RejectedPackets == 0 && peer.WatchdogStops == 0 &&
                    !peer.Transmit.Enabled && !peer.Transmit.Ptt && peer.Transmit.Packets == 0,
                    "Simulator receive-only invariant failed.");
                if (state.IqPackets != lastIq) { lastIq = state.IqPackets; iqAt = now; }
                if (state.AudioProduced != lastAudio) { lastAudio = state.AudioProduced; audioAt = now; }
                Require(now - iqAt < 5 && now - audioAt < 5 && now - spectrumAt < 5, "Receive I/Q, audio or spectrum stalled for five seconds.");
                bool hold = slow && now >= 1 && now < 2;
                held |= hold;
                if (!hold)
                {
                    int count = session.ReadAudio(audio); result.AudioFrames += count;
                    for (int i = 0; i < 2 * count; ++i) Require(double.IsFinite(audio[i]), "Non-finite audio.");
                    if (!loss && !slow && now - tunedAt >= 1.2)
                        result.AudioChecks += signal.Add(audio.AsSpan(0, 2 * count), 14_200_000 - center);
                    var frame = session.ReadSpectrum();
                    if (frame is not null)
                    {
                        Require(frame.Sequence > lastSpectrum && frame.PublishedMonotonicMilliseconds >= lastPublication,
                            "Duplicate spectrum or backwards timestamp.");
                        Require(frame.TuningGeneration == generation && frame.RequestedCenterFrequencyHz == center &&
                            frame.Ddc == 2 && frame.SampleRate == 192000, "Incorrect spectrum configuration/epoch.");
                        int peak = 0;
                        for (int i = 0; i < frame.LevelsDb.Length; ++i)
                        {
                            Require(float.IsFinite(frame.LevelsDb.Span[i]), "Non-finite spectrum.");
                            if (frame.LevelsDb.Span[i] > frame.LevelsDb.Span[peak]) peak = i;
                        }
                        if (!loss && !slow && now - tunedAt >= 1.2)
                        {
                            Require(Math.Abs(frame.FrequencyAt(peak) - 14_200_000) <= frame.BinWidthHz &&
                                Math.Abs(frame.LevelsDb.Span[peak] - 20 * Math.Log10(0.25)) <= 1.6,
                                $"Incorrect spectrum tone/level: {frame.FrequencyAt(peak):F3} Hz RF, {frame.LevelsDb.Span[peak]:F3} dB.");
                            ++result.SpectrumChecks;
                        }
                        if (firstSpectrum == 0) { firstSpectrum = frame.Sequence; firstCoalesced = frame.CoalescedFrames; }
                        if (lastSpectrum != 0) result.MaxSpectrumGapMs = Math.Max(result.MaxSpectrumGapMs, (now - spectrumAt) * 1000);
                        lastSpectrum = frame.Sequence; lastPublication = frame.PublishedMonotonicMilliseconds;
                        spectrumAt = now; ++result.SpectrumFrames;
                        result.ProducedSpectrumFrames = lastSpectrum - firstSpectrum + 1;
                        result.CoalescedFrames = frame.CoalescedFrames - firstCoalesced;
                    }
                }
                result.ElapsedSeconds = now;
                if (now >= nextResource) { resources.Sample(); nextResource = now + 5; }
                if (now >= nextProgress)
                {
                    progress?.Invoke(new(name, now, state.IqPackets, result.AudioFrames, result.SpectrumFrames, resources.WorkingSet));
                    nextProgress = now + 30;
                }
                await Task.Delay(5, token);
            }
            var final = session.State;
            Require(result.AudioFrames > 0 && result.SpectrumFrames > 1, "No advancing audio/spectrum observations.");
            if (!loss && !slow) Require(result.AudioChecks > 0 && result.SpectrumChecks > 0, "No settled signal checks completed.");
            if (loss) Require(final.MissingPackets > initial.MissingPackets && simulator.State.InjectedDrops > 0, "Packet loss injection was not observed.");
            if (slow) Require(held && final.AudioDropped > initial.AudioDropped && result.CoalescedFrames > 0,
                "Slow reader did not exercise bounded audio drops and spectrum coalescing.");
            ValidateState(final, loss, slow);
        }
        finally
        {
            result.ElapsedSeconds = clock.Elapsed.TotalSeconds;
            result.Resources = resources.Finish(result.ElapsedSeconds);
            progress?.Invoke(new(name, result.ElapsedSeconds, lastIq, result.AudioFrames, result.SpectrumFrames, resources.WorkingSet));
        }
    }

    internal static void ValidateState(ReceiveState state, bool loss, bool slow)
    {
        Require(state.DspErrors == 0 && state.SocketErrors == 0 && state.InputOverruns == 0, "Unexpected native socket/DSP/input-overrun error.");
        Require(state.MalformedPackets == 0 && state.ForeignPackets == 0 && state.LatePackets == 0, "Unexpected malformed/foreign/late traffic.");
        Require(state.AudioQueued is >= 0 and <= 16384, "Audio queue exceeded its bound.");
        Require(loss || state.MissingPackets == 0, "Unplanned packet loss.");
        Require(slow || state.AudioDropped == 0, "Unplanned audio reader drops.");
    }
    internal static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static bool IsMeasurementFailure(Exception ex) => ex is IOException or InvalidOperationException or TimeoutException or SocketException;
    private static async Task WaitUntil(Func<bool> predicate, int milliseconds, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Timed out waiting for receive shutdown/failure.");
            await Task.Delay(10, token);
        }
    }
}

internal sealed class ReceiveSoakObservation
{
    public double ElapsedSeconds, MaxSpectrumGapMs;
    public int Retunes, AudioChecks, SpectrumChecks;
    public long AudioFrames, SpectrumFrames, ProducedSpectrumFrames, CoalescedFrames;
    public double SpectrumCadence => ElapsedSeconds > 0 ? ProducedSpectrumFrames / ElapsedSeconds : 0;
    public ReceiveSoakResources? Resources;
}

internal sealed class ReceiveSoakSignal
{
    private int frames, crossings;
    private double energy, previous;
    public void Reset() { frames = crossings = 0; energy = previous = 0; }
    public int Add(ReadOnlySpan<double> stereo, double expectedHz)
    {
        int checks = 0;
        for (int i = 0; i < stereo.Length; i += 2)
        {
            double value = stereo[i];
            if (frames != 0 && previous <= 0 && value > 0) ++crossings;
            previous = value; energy += value * value;
            if (++frames < 8192) continue;
            double rms = Math.Sqrt(energy / frames), tone = crossings * 48000.0 / (frames - 1);
            ReceiveSoak.Require(Math.Abs(rms - 0.25 / Math.Sqrt(2)) < 0.01 && Math.Abs(tone - expectedHz) < 20,
                $"Incorrect settled audio: RMS={rms:G6}, tone={tone:F3} Hz; expected {expectedHz} Hz.");
            Reset(); ++checks;
        }
        return checks;
    }
}

internal sealed class ReceiveSoakResourceSampler : IDisposable
{
    private readonly Process process = Process.GetCurrentProcess();
    private readonly long initialWorkingSet, initialPrivate, initialManaged, initialAllocated;
    private readonly double initialCpu;
    private readonly int initialThreads;
    private long samples, peakWorkingSet;
    public long WorkingSet { get; private set; }
    public ReceiveSoakResourceSampler()
    {
        Sample(); initialWorkingSet = WorkingSet; initialPrivate = process.PrivateMemorySize64;
        initialManaged = GC.GetTotalMemory(false); initialAllocated = GC.GetTotalAllocatedBytes();
        initialCpu = process.TotalProcessorTime.TotalSeconds; initialThreads = process.Threads.Count;
    }
    public void Sample()
    { process.Refresh(); WorkingSet = process.WorkingSet64; peakWorkingSet = Math.Max(peakWorkingSet, WorkingSet); ++samples; }
    public ReceiveSoakResources Finish(double elapsed)
    {
        Sample(); double cpu = process.TotalProcessorTime.TotalSeconds - initialCpu;
        return new(samples, initialWorkingSet, WorkingSet, peakWorkingSet, WorkingSet - initialWorkingSet,
            initialPrivate > 0 ? initialPrivate : null, process.PrivateMemorySize64 > 0 ? process.PrivateMemorySize64 : null,
            initialManaged, GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes() - initialAllocated, cpu, elapsed > 0 ? cpu / elapsed * 100 : 0,
            initialThreads, process.Threads.Count);
    }
    public void Dispose() => process.Dispose();
}
