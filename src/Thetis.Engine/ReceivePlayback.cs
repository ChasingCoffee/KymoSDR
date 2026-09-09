using System.Diagnostics;
using Thetis.Audio;

namespace Thetis.Engine;

public sealed record PlaybackSnapshot(ReceiveState Receive, PlaybackState Output, ReceiveSpectrumFrame? Spectrum,
    ReceiveDemodulationState Demodulation, ReceiveGainState Gain, double NullRms, double NullToneHz,
    G2ReceiveSafetyState? Hardware = null);

/// <summary>Background PCM/spectrum pump. It is the sole reader of a receive session.
/// Stop/join this pump before disposing its receiver. It owns the output, not the radio.</summary>
public sealed class ReceivePlayback : IAsyncDisposable
{
    private readonly ReceiveSession receiver;
    private PlaybackOutput? output;
    private readonly object outputGate = new();
    private readonly SemaphoreSlim outputChange = new(1,1);
    private PlaybackState detachedState;
    private long generation = 1,discardedFrames;
    private bool finished;
    private readonly Func<G2ReceiveSafetyState?> readSafety;
    private readonly ReceiveAudioCapture? capture;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly object disposeGate = new();
    private Task? disposal;
    private PlaybackSnapshot? snapshot;
    private Exception? error;
    public PlaybackSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public Exception? Error => Volatile.Read(ref error);
    public Task Completion => worker;
    public ReceivePlayback(ReceiveSession receiver, PlaybackOutput output,ReceiveAudioCapture? capture = null) : this(receiver,output,
        () => receiver is G2ReceiveSession g2 ? g2.Safety : null,capture) { }
    internal ReceivePlayback(ReceiveSession receiver,PlaybackOutput output,Func<G2ReceiveSafetyState?> readSafety,ReceiveAudioCapture? capture = null)
    {
        this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.readSafety = readSafety;
        this.capture = capture;
        // Explicit mute is required again even when the caller reused settings.
        output.SetMuted(true);
        detachedState = output.State;
        worker = Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public void SetMuted(bool muted) { lock (outputGate) CurrentOutput().SetMuted(muted); }
    public void Flush() { lock (outputGate) CurrentOutput().Flush(); }
    private PlaybackOutput CurrentOutput() => output ?? throw new InvalidOperationException("Audio output is changing.");
    private PlaybackState Decorate(PlaybackState state) => state with
    { Generation = generation,Switching = output is null && !finished,SwitchDiscardedFrames = discardedFrames };

    /// <summary>Join the old output before opening the next one. The pump keeps
    /// consuming receive PCM while no output exists; those frames are counted,
    /// not queued for later playback. No radio or capture owner is restarted.</summary>
    public async Task ReplaceOutputAsync(Func<PlaybackOutput> open,bool muted,CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(open);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token,stop.Token);
        await outputChange.WaitAsync(cancel.Token).ConfigureAwait(false);
        bool began = false;
        try
        {
            await Task.Run(() =>
            {
                cancel.Token.ThrowIfCancellationRequested();
                PlaybackOutput old;
                lock (outputGate)
                {
                    if (finished) throw new InvalidOperationException("Receive playback has stopped.");
                    old = CurrentOutput(); old.SetMuted(true);
                    detachedState = old.State with { Active = false,Muted = true,Queued = 0,Levels = PlaybackLevels.Silence };
                    output = null; ++generation; began = true;
                }
                // Native driver calls may block. Never hold the pump lock here.
                old.Dispose(); cancel.Token.ThrowIfCancellationRequested();
                PlaybackOutput? replacement = open();
                try
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    lock (outputGate)
                    {
                        if (finished) throw new InvalidOperationException("Receive stopped during the audio change.");
                        cancel.Token.ThrowIfCancellationRequested();
                        replacement.SetMuted(muted);
                        output = replacement; replacement = null;
                    }
                }
                finally { replacement?.Dispose(); }
            },cancel.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (began) { Interlocked.CompareExchange(ref error,ex,null); stop.Cancel(); }
            throw;
        }
        finally { outputChange.Release(); }
    }
    private void Run()
    {
        double[] input = new double[4096]; float[] rendered = new float[1920];
        var clock = Stopwatch.StartNew(); double nextSnapshot = 0;
        ReceiveSpectrumFrame? spectrum = null;
        G2ReceiveSafetyState? safety = null;
        double energy = 0, rms = 0, hz = 0, previous = 0; int measured = 0, crossings = 0;
        try
        {
            var device = detachedState;
            PlaybackOutput? previousOutput = null;
            while (!stop.IsCancellationRequested)
            {
                int frames = receiver.ReadAudio(input);
                if (frames > 0) capture?.Add(input,frames);
                double now = clock.Elapsed.TotalSeconds;
                PlaybackState observed;
                lock (outputGate)
                {
                    var current = output;
                    if (!ReferenceEquals(current,previousOutput))
                    {
                        energy = rms = hz = previous = 0; measured = crossings = 0;
                        previousOutput = current;
                        if (current is not null) device = current.State;
                    }
                    if (current is null) { discardedFrames += frames; device = detachedState; }
                    else if (frames > 0) current.Write(input,frames);
                    if (current is not null && !device.ClockTracking)
                    {
                        // Reserve native prefill/FIR look-ahead. Queue occupancy
                        // also handles flush; input credit alone can become stale.
                        int block = device.Rate/100;
                        for (int burst = 0; burst < 5 && RenderMonitorBlock(current,rendered,block); ++burst)
                        {
                            for (int i = 0; i < block; ++i)
                            {
                                double v = rendered[2*i]; energy += v*v;
                                if (v > 0 && previous <= 0) ++crossings; previous = v;
                                if (++measured == device.Rate/10)
                                { rms = Math.Sqrt(energy/measured); hz = rms > 1e-8 ? crossings*10 : 0; energy = 0; measured = crossings = 0; }
                            }
                        }
                    }
                    if (now >= nextSnapshot && current is not null)
                    { device = current.State; if (!device.Active) throw OutputFailure(device); }
                    observed = now >= nextSnapshot ? Decorate(device) : device;
                }
                if (now >= nextSnapshot)
                {
                    spectrum = receiver.ReadSpectrum() ?? spectrum;
                    var state = receiver.State;
                    safety = readSafety();
                    if (safety is { WorkerRunning:false })
                    {
                        if (safety.StopReason != 1) throw new IOException($"G2 receive stopped safely (reason {safety.StopReason}, key bits {safety.KeyBitsSeen}).");
                        break; // native deadline is normal completion, not a dead pump left allocated
                    }
                    if (state.SocketWorkers != 1 || state.SocketErrors != 0 || state.DspErrors != 0)
                        throw new IOException("The receive worker stopped or reported a transport/DSP error.");
                    Volatile.Write(ref snapshot,new(state,observed,spectrum,receiver.Demodulation,receiver.Gain,rms,hz,safety));
                    nextSnapshot = now+.025;
                }
                if (frames == 0) Thread.Sleep(2);
            }
        }
        catch (Exception ex)
        {
            // A write can observe the native fault before the next snapshot.
            // Preserve the specific latched reason instead of a generic write error.
            try { lock (outputGate) { if (output is { } current && !current.State.Active) ex = OutputFailure(current.State); } } catch { /* retain original failure */ }
            Volatile.Write(ref error,ex);
        }
        finally
        {
            capture?.Freeze();
            lock (outputGate)
            {
                finished = true;
                try { output?.SetMuted(true); } catch (Exception ex) { Interlocked.CompareExchange(ref error,ex,null); }
                // Preserve terminal counters before Dispose closes native output.
                try
                {
                    Volatile.Write(ref snapshot,new(receiver.State,Decorate(output?.State ?? detachedState),spectrum,receiver.Demodulation,receiver.Gain,rms,hz,safety));
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref error,ex,null); }
            }
        }
    }
    private static IOException OutputFailure(PlaybackState state) => new(
        $"Audio output stopped: {state.Fault} (driver status {state.DriverStatus}). Refresh devices, select an output and reconnect; playback will start muted.");
    internal static bool RenderMonitorBlock(PlaybackOutput output,float[] samples,int block)
    {
        // One 10 ms output block consumes 480 source frames at all supported
        // rates. Retain 1024 source frames; never drain the FIR's final look-ahead.
        if (output.QueuedSourceFrames < 1024+480) return false;
        output.RenderNull(samples,block);
        return true;
    }
    public ValueTask DisposeAsync()
    { lock (disposeGate) return new(disposal ??= DisposeCore()); }
    private async Task DisposeCore()
    {
        stop.Cancel();
        try { await worker.ConfigureAwait(false); }
        finally
        {
            await outputChange.WaitAsync().ConfigureAwait(false);
            try
            {
                PlaybackOutput? current;
                lock (outputGate) { current = output; output = null; }
                if (current is not null) await Task.Run(current.Dispose).ConfigureAwait(false);
            }
            finally { outputChange.Release(); stop.Dispose(); }
        }
    }
}
