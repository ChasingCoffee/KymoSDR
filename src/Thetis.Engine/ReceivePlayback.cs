using System.Diagnostics;
using Thetis.Audio;

namespace Thetis.Engine;

public sealed record PlaybackSnapshot(ReceiveState Receive, PlaybackState Output, ReceiveSpectrumFrame? Spectrum,
    ReceiveDemodulationState Demodulation, ReceiveGainState Gain, double NullRms, double NullToneHz);

/// <summary>Background PCM/spectrum pump. It is the sole reader of a receive session.
/// Stop/join this pump before disposing its receiver. It owns the output, not the radio.</summary>
public sealed class ReceivePlayback : IAsyncDisposable
{
    private readonly ReceiveSession receiver;
    private readonly PlaybackOutput output;
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly object disposeGate = new();
    private Task? disposal;
    private PlaybackSnapshot? snapshot;
    private Exception? error;
    public PlaybackSnapshot? Snapshot => Volatile.Read(ref snapshot);
    public Exception? Error => Volatile.Read(ref error);
    public Task Completion => worker;
    public ReceivePlayback(ReceiveSession receiver, PlaybackOutput output)
    {
        this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        // Explicit mute is required again even when the caller reused settings.
        output.SetMuted(true);
        worker = Task.Factory.StartNew(Run,CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public void SetMuted(bool muted) => output.SetMuted(muted);
    public void Flush() => output.Flush();
    private void Run()
    {
        double[] input = new double[4096]; float[] rendered = new float[1920];
        var clock = Stopwatch.StartNew(); double nextSnapshot = 0;
        ReceiveSpectrumFrame? spectrum = null;
        double energy = 0, rms = 0, hz = 0, previous = 0; int measured = 0, crossings = 0;
        try
        {
            var device = output.State;
            while (!stop.IsCancellationRequested)
            {
                int frames = receiver.ReadAudio(input);
                if (frames > 0) output.Write(input,frames);
                double now = clock.Elapsed.TotalSeconds;
                if (!device.ClockTracking)
                {
                    // Sample-driven with an explicit reserve for native prefill
                    // and FIR look-ahead. Queue occupancy also handles flush:
                    // input credit alone becomes stale when queued PCM is discarded.
                    int block = device.Rate/100;
                    for (int burst = 0; burst < 5 && RenderMonitorBlock(output,rendered,block); ++burst)
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
                if (now >= nextSnapshot)
                {
                    spectrum = receiver.ReadSpectrum() ?? spectrum;
                    var state = receiver.State; device = output.State;
                    if (!device.Active) throw OutputFailure(device);
                    if (state.SocketWorkers != 1 || state.SocketErrors != 0 || state.DspErrors != 0)
                        throw new IOException("The receive worker stopped or reported a transport/DSP error.");
                    Volatile.Write(ref snapshot,new(state,device,spectrum,receiver.Demodulation,receiver.Gain,rms,hz));
                    nextSnapshot = now+.025;
                }
                if (frames == 0) Thread.Sleep(2);
            }
        }
        catch (Exception ex)
        {
            // A write can observe the native fault before the next snapshot.
            // Preserve the specific latched reason instead of a generic write error.
            try { var state = output.State; if (!state.Active) ex = OutputFailure(state); } catch { /* retain original failure */ }
            Volatile.Write(ref error,ex);
        }
        finally
        {
            try { output.SetMuted(true); } catch (Exception ex) { Interlocked.CompareExchange(ref error,ex,null); }
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
        finally { try { output.Dispose(); } finally { stop.Dispose(); } }
    }
}
