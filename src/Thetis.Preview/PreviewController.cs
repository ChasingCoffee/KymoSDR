using Thetis.Audio;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Preview;

public sealed record PreviewSettings(int FrequencyHz = 14_199_000, ReceiveMode Mode = ReceiveMode.Usb,
    int LowCutHz = 300, int HighCutHz = 3000, int AudioGainDb = -40, bool Muted = true,
    ReceiveAgcMode AgcMode = ReceiveAgcMode.Medium, int AgcMaxGainDb = 60)
{
    public void Validate()
    {
        if (FrequencyHz is < 0 or > 61_440_000) throw new ArgumentOutOfRangeException(nameof(FrequencyHz));
        if (Mode is not (ReceiveMode.Usb or ReceiveMode.Lsb) || LowCutHz < 0 || HighCutHz > 12000 || HighCutHz-LowCutHz < 100)
            throw new ArgumentException("Choose USB/LSB and filter edges 0–12000 Hz, at least 100 Hz apart.");
        if (AudioGainDb is < -60 or > 0 || AgcMaxGainDb is < 0 or > 80 ||
            AgcMode is not (ReceiveAgcMode.Off or ReceiveAgcMode.Slow or ReceiveAgcMode.Medium or ReceiveAgcMode.Fast))
            throw new ArgumentException("AF gain must be −60..0 dB and AGC maximum 0..80 dB, with a supported preset.");
    }
    internal ReceiveDemodulation Demodulation => new(Mode,LowCutHz,HighCutHz);
    internal ReceiveGain Gain => new(AudioGainDb,Muted,AgcMode,AgcMaxGainDb);
}

/// <summary>Application owner shared by the desktop and headless playback campaign.
/// Owns exactly one IPv4 loopback simulator; there is no address/TX/radio-selection API.</summary>
public sealed class PreviewController : IAsyncDisposable
{
    private readonly Func<string,PlaybackDevice?,PlaybackOutput> openOutput;
    public PreviewController() : this((directory,device) => device is null ? PlaybackOutput.OpenNull(directory) : PlaybackOutput.OpenDevice(directory,device)) { }
    internal PreviewController(Func<string,PlaybackDevice?,PlaybackOutput> openOutput) => this.openOutput = openOutput;
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly object cancellationGate = new();
    private CancellationTokenSource? connecting;
    private bool disposed;
    private ReceiveSession? receiver;
    private ReceivePlayback? playback;
    private Task? observation;
    private Exception? lastError;
    private G2Simulator? p2;
    private P1Simulator? p1;
    private PreviewSettings settings = new();
    public PreviewSettings Settings => Volatile.Read(ref settings);
    public PlaybackSnapshot? Snapshot => Volatile.Read(ref playback)?.Snapshot;
    public Exception? Error => Volatile.Read(ref lastError) ?? Volatile.Read(ref playback)?.Error;
    public bool Connected => Volatile.Read(ref playback) is not null;

    public async Task ConnectAsync(string directory,int protocol = 2,PlaybackDevice? device = null,
        CancellationToken token = default)
    {
        if (protocol is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(protocol));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (receiver is not null) throw new InvalidOperationException("Disconnect before changing simulator or audio output.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (cancellationGate) connecting = startup;
            try
            {
                await Task.Run(() =>
                {
                    startup.Token.ThrowIfCancellationRequested();
                    // Reconnect never restores audible output or a high AF level.
                    var initial = Settings with { Muted = true, AudioGainDb = Math.Min(Settings.AudioGainDb,-40) };
                    PlaybackOutput? output = null;
                    try
                    {
                        if (protocol == 1) { p1 = P1Simulator.Open(); receiver = P1ReceiveSession.Open(directory,new(p1.Port,initial.FrequencyHz,Demodulation:initial.Demodulation,Gain:initial.Gain),startup.Token); }
                        else { p2 = G2Simulator.Open(new(BasePort:0)); receiver = P2ReceiveSession.Open(directory,new(p2.BasePort,FrequencyHz:initial.FrequencyHz,Demodulation:initial.Demodulation,Gain:initial.Gain),startup.Token); }
                        output = openOutput(directory,device);
                        startup.Token.ThrowIfCancellationRequested();
                        playback = new(receiver,output); output = null;
                        Volatile.Write(ref settings,initial);
                        Volatile.Write(ref lastError,null);
                        observation = ObservePlayback(playback);
                    }
                    finally { output?.Dispose(); }
                },startup.Token).ConfigureAwait(false);
            }
            catch { await CloseOwned().ConfigureAwait(false); throw; }
            finally { lock (cancellationGate) connecting = null; }
        }
        finally { gate.Release(); }
    }
    private async Task ObservePlayback(ReceivePlayback owned)
    {
        await owned.Completion.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(playback,owned) || owned.Error is not { } failure) return;
            Volatile.Write(ref lastError,failure);
            try { await CloseOwned().ConfigureAwait(false); }
            catch (Exception ex) { Volatile.Write(ref lastError,new AggregateException("Playback failed and cleanup reported an error.",failure,ex)); }
        }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(PreviewSettings value,CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value); value.Validate();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var rx = receiver ?? throw new InvalidOperationException("Connect the simulator first.");
            var pump = playback ?? throw new InvalidOperationException("Playback is not running.");
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                pump.SetMuted(true);
                try
                {
                    rx.ConfigureGain(value.Gain); rx.ConfigureDemodulation(value.Demodulation);
                    if (value.FrequencyHz != Settings.FrequencyHz) rx.Tune(value.FrequencyHz);
                    pump.Flush(); pump.SetMuted(value.Muted);
                    Volatile.Write(ref settings,value);
                }
                catch { Volatile.Write(ref settings,Settings with { Muted = true }); throw; }
            },token).ConfigureAwait(false);
        }
        catch { await CloseOwned().ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        CancelStartup(); await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseOwned().ConfigureAwait(false); Volatile.Write(ref lastError,null); }
        finally { gate.Release(); }
    }
    private void CancelStartup() { lock (cancellationGate) connecting?.Cancel(); }
    private async Task CloseOwned()
    {
        var pump = playback;
        try { if (pump is not null) await pump.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (receiver is not null) await Task.Run(receiver.Dispose).ConfigureAwait(false); }
            finally
            {
                receiver = null;
                try { if (p1 is not null) await p1.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { if (p2 is not null) await p2.DisposeAsync().ConfigureAwait(false); }
                    finally
                    {
                        p1 = null; p2 = null;
                        Volatile.Write(ref settings,Settings with { Muted = true });
                        // Publish disconnected only after every owned worker
                        // has joined, not while native receiver close is running.
                        Volatile.Write(ref playback,null);
                    }
                }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        CancelStartup(); await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; await CloseOwned().ConfigureAwait(false); } }
        finally { gate.Release(); }
        if (observation is { } pending) await pending.ConfigureAwait(false);
    }
}
