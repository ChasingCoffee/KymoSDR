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
        int nullSourceCredit = 0;
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
                if (!device.Physical)
                {
                    // The silent monitor is sample-driven, not a fake hardware
                    // clock. A delayed producer must not render a wall-clock
                    // catch-up burst before draining PCM still in CM's queue.
                    // Each read is <=2048 source frames, so this is <=5 blocks.
                    nullSourceCredit += frames;
                    int block = device.Rate/100;
                    while (nullSourceCredit >= 480)
                    {
                        output.RenderNull(rendered,block); nullSourceCredit -= 480;
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
                    if (!device.Active) throw new IOException("The audio output stopped or was disconnected. Reconnect and select an available output.");
                    if (state.SocketWorkers != 1 || state.SocketErrors != 0 || state.DspErrors != 0)
                        throw new IOException("The receive worker stopped or reported a transport/DSP error.");
                    Volatile.Write(ref snapshot,new(state,device,spectrum,receiver.Demodulation,receiver.Gain,rms,hz));
                    nextSnapshot = now+.025;
                }
                if (frames == 0) Thread.Sleep(2);
            }
        }
        catch (Exception ex) { Volatile.Write(ref error,ex); }
        finally
        {
            try { output.SetMuted(true); } catch (Exception ex) { Interlocked.CompareExchange(ref error,ex,null); }
        }
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
