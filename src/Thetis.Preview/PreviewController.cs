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
    public PreviewSettings ForListening(bool hardware) => hardware
        ? this with { Muted = false,AudioGainDb = -10,AgcMode = ReceiveAgcMode.Medium,AgcMaxGainDb = 80 }
        : this with { Muted = false }; // simulator unmute never adds gain
}

/// <summary>Application owner shared by the desktop and headless playback campaign.
/// Owns one loopback simulator or explicitly verified bounded G2 receive session. No TX API.</summary>
public sealed class PreviewController : IAsyncDisposable
{
    private readonly Func<string,PlaybackDevice?,PlaybackOutput> openOutput;
    private readonly Func<string,G2HardwareRequest,CancellationToken,ReceiveSession> openHardware;
    public PreviewController() : this((directory,device) => device is null ? PlaybackOutput.OpenNull(directory) : PlaybackOutput.OpenDevice(directory,device)) { }
    internal PreviewController(Func<string,PlaybackDevice?,PlaybackOutput> openOutput,
        Func<string,G2HardwareRequest,CancellationToken,ReceiveSession>? openHardware = null)
    {
        this.openOutput = openOutput;
        this.openHardware = openHardware ?? ((directory,request,token) => G2ReceiveSession.Open(directory,G2Preflight.Verify(request,token),token));
    }
    private readonly SemaphoreSlim gate = new(1,1);
    private readonly object cancellationGate = new();
    private CancellationTokenSource? connecting;
    private bool disposed;
    private ReceiveSession? receiver;
    private ReceivePlayback? playback;
    private string? nativeDirectory;
    private PlaybackDevice? outputDevice;
    public PlaybackDevice? CurrentOutput => Volatile.Read(ref outputDevice);
    private Task? observation;
    private readonly CancellationTokenSource diagnosticsStop = new();
    private Task? diagnosticSampling;
    private readonly object disposalGate = new();
    private Task? disposal;
    private sealed record ObservedSession(long Id,ReceivePlayback Pump);
    private ObservedSession? observedSession;
    public PreviewDiagnostics Diagnostics { get; } = new();
    private Exception? lastError;
    private G2Simulator? p2;
    private P1Simulator? p1;
    private PreviewSettings settings = new();
    public PreviewSettings Settings => Volatile.Read(ref settings);
    public PlaybackSnapshot? Snapshot => Volatile.Read(ref playback)?.Snapshot;
    public Exception? Error => Volatile.Read(ref lastError) ?? Volatile.Read(ref playback)?.Error;
    public bool Connected => Volatile.Read(ref playback) is not null;
    private bool hardwareConnected;
    public bool HardwareConnected => Connected && Volatile.Read(ref hardwareConnected);
    private PlaybackSnapshot? lastSnapshot;
    public PlaybackSnapshot? LastSnapshot => Volatile.Read(ref lastSnapshot);

    public Task ConnectG2Async(string directory,G2HardwareRequest request,PlaybackDevice? device = null,
        CancellationToken token = default,PreviewSettings? initialSettings = null,ReceiveAudioCapture? capture = null)
    {
        ArgumentNullException.ThrowIfNull(request); request.Validate();
        var initial = initialSettings ?? new(FrequencyHz:request.FrequencyHz,LowCutHz:100);
        if (initial.FrequencyHz != request.FrequencyHz) throw new ArgumentException("G2 request and receive controls must specify the same frequency.");
        return ConnectCore(directory,2,device,token,initial,request,capture);
    }

    public async Task ConnectAsync(string directory,int protocol = 2,PlaybackDevice? device = null,
        CancellationToken token = default,PreviewSettings? initialSettings = null)
        => await ConnectCore(directory,protocol,device,token,initialSettings,null).ConfigureAwait(false);
    private async Task ConnectCore(string directory,int protocol,PlaybackDevice? device,CancellationToken token,
        PreviewSettings? initialSettings,G2HardwareRequest? hardware,ReceiveAudioCapture? capture = null)
    {
        if (protocol is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(protocol));
        initialSettings?.Validate();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (receiver is not null) throw new InvalidOperationException("Disconnect before changing receive source.");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (cancellationGate) connecting = startup;
            try
            {
                await Task.Run(() =>
                {
                    startup.Token.ThrowIfCancellationRequested();
                    // Reconnect never restores audible output or a high AF level.
                    var requested = initialSettings ?? Settings;
                    var initial = requested with { Muted = true, AudioGainDb = Math.Min(requested.AudioGainDb,-40) };
                    PlaybackOutput? output = null;
                    try
                    {
                        if (hardware is not null)
                        {
                            receiver = openHardware(directory,hardware,startup.Token);
                            receiver.ConfigureGain(initial.Gain); receiver.ConfigureDemodulation(initial.Demodulation);
                        }
                        else if (protocol == 1) { p1 = P1Simulator.Open(); receiver = P1ReceiveSession.Open(directory,new(p1.Port,initial.FrequencyHz,Demodulation:initial.Demodulation,Gain:initial.Gain),startup.Token); }
                        else { p2 = G2Simulator.Open(new(BasePort:0)); receiver = P2ReceiveSession.Open(directory,new(p2.BasePort,FrequencyHz:initial.FrequencyHz,Demodulation:initial.Demodulation,Gain:initial.Gain),startup.Token); }
                        output = openOutput(directory,device);
                        startup.Token.ThrowIfCancellationRequested();
                        playback = new(receiver,output,capture); output = null;
                        nativeDirectory = directory; Volatile.Write(ref outputDevice,device);
                        Volatile.Write(ref hardwareConnected,hardware is not null); Volatile.Write(ref lastSnapshot,null);
                        Volatile.Write(ref settings,initial);
                        Volatile.Write(ref lastError,null);
                        Volatile.Write(ref observedSession,new(Diagnostics.Begin(protocol,initial,hardware is not null),playback));
                        RecordDiagnostics();
                        diagnosticSampling ??= Task.Run(SampleDiagnostics);
                        observation = ObservePlayback(playback);
                    }
                    finally { output?.Dispose(); }
                },startup.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { Diagnostics.Event("connect-failed",ex); await CloseOwned(ex).ConfigureAwait(false); throw; }
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
            if (!ReferenceEquals(playback,owned)) return;
            var failure = owned.Error;
            Volatile.Write(ref lastError,failure);
            try { await CloseOwned(failure).ConfigureAwait(false); }
            catch (Exception ex) { Volatile.Write(ref lastError,failure is null ? ex : new AggregateException("Playback failed and cleanup reported an error.",failure,ex)); }
        }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(PreviewSettings value,CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(value); value.Validate();
        if (HardwareConnected && value.FrequencyHz is < 14_000_000 or > 14_350_000)
            throw new ArgumentException("This G2 receive profile is restricted to 14.000–14.350 MHz.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var rx = receiver ?? throw new InvalidOperationException("Connect a receive source first.");
            var pump = playback ?? throw new InvalidOperationException("Playback is not running.");
            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                pump.SetMuted(true);
                try
                {
                    rx.ConfigureGain(value.Gain);
                    if (value.Demodulation != Settings.Demodulation) rx.ConfigureDemodulation(value.Demodulation);
                    if (value.FrequencyHz != Settings.FrequencyHz) rx.Tune(value.FrequencyHz);
                    pump.Flush(); pump.SetMuted(value.Muted);
                    Volatile.Write(ref settings,value);
                    Diagnostics.Event("controls-applied",controls:value);
                }
                catch { Volatile.Write(ref settings,Settings with { Muted = true }); throw; }
            },token).ConfigureAwait(false);
        }
        catch (Exception ex) { Diagnostics.Event("controls-failed",ex); await CloseOwned(ex).ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }
    public Task StartListeningAsync(CancellationToken token = default) => ApplyAsync(Settings.ForListening(HardwareConnected),token);
    public Task SwitchOutputAsync(PlaybackDevice? device,CancellationToken token = default) =>
        ChangeOutputAsync(_ => device,token);

    public async Task<IReadOnlyList<PlaybackDevice>> RefreshOutputsAsync(
        Func<string,IReadOnlyList<PlaybackDevice>> enumerate,CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        IReadOnlyList<PlaybackDevice> devices = [];
        await ChangeOutputAsync(directory =>
        {
            // PortAudio can be reinitialized only after the old stream is joined.
            // Match the active route, never a pending GUI choice or system default.
            var saved = CurrentOutput is { } current ? AudioOutputPreferences.From(current,current.SelectedPair) : null;
            devices = enumerate(directory);
            return saved is null ? null : saved.Match(devices) ?? throw new IOException("The active output disappeared or changed. Receive stopped; choose an output and reconnect.");
        },token).ConfigureAwait(false);
        return devices;
    }
    private async Task ChangeOutputAsync(Func<string,PlaybackDevice?> select,CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var pump = playback ?? throw new InvalidOperationException("Connect a receive source first.");
            string directory = nativeDirectory ?? throw new InvalidOperationException("The receive owner has no native directory.");
            using var change = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (cancellationGate) connecting = change;
            Diagnostics.Event("output-change-started");
            PlaybackDevice? selected = null;
            try
            {
                await pump.ReplaceOutputAsync(() =>
                {
                    selected = select(directory); change.Token.ThrowIfCancellationRequested();
                    return openOutput(directory,selected);
                },Settings.Muted,change.Token).ConfigureAwait(false);
                Volatile.Write(ref outputDevice,selected);
                Diagnostics.Event("output-changed"); RecordDiagnostics();
            }
            catch (Exception ex)
            {
                Diagnostics.Event("output-change-failed",ex);
                Volatile.Write(ref lastError,ex is OperationCanceledException ? null : ex);
                await CloseOwned(ex).ConfigureAwait(false); throw;
            }
            finally { lock (cancellationGate) connecting = null; }
        }
        finally { gate.Release(); }
    }
    public async Task DisconnectAsync()
    {
        CancelStartup(); await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseOwned().ConfigureAwait(false); Volatile.Write(ref lastError,null); }
        finally { gate.Release(); }
    }
    private void CancelStartup() { lock (cancellationGate) connecting?.Cancel(); }
    private async Task SampleDiagnostics()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try { while (await timer.WaitForNextTickAsync(diagnosticsStop.Token).ConfigureAwait(false)) RecordDiagnostics(); }
        catch (OperationCanceledException) when (diagnosticsStop.IsCancellationRequested) { }
    }
    private void RecordDiagnostics()
    {
        var observed = Volatile.Read(ref observedSession); if (observed is null) return;
        try
        {
            var simulator = p2?.State;
            Diagnostics.Record(observed.Id,observed.Pump.Snapshot,simulator is null ? null : new(simulator.IqPacingResyncs,simulator.IqPacingLostNanoseconds));
        }
        catch (Exception ex) { Diagnostics.Event("diagnostic-sample-unavailable",ex); }
    }
    private async Task CloseOwned(Exception? cause = null)
    {
        var pump = playback;
        Exception? closeError = null;
        try { await CloseOwnedCore().ConfigureAwait(false); }
        catch (Exception ex) { closeError = ex; throw; }
        finally
        {
            RecordDiagnostics();
            if (Interlocked.Exchange(ref observedSession,null) is { } observed) Diagnostics.End(observed.Id,cause ?? closeError ?? pump?.Error);
        }
    }
    private async Task CloseOwnedCore()
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
                        RecordDiagnostics();
                        Volatile.Write(ref lastSnapshot,pump?.Snapshot);
                        Volatile.Write(ref hardwareConnected,false);
                        p1 = null; p2 = null;
                        nativeDirectory = null; Volatile.Write(ref outputDevice,null);
                        Volatile.Write(ref settings,Settings with { Muted = true });
                        // Publish disconnected only after every owned worker
                        // has joined, not while native receiver close is running.
                        Volatile.Write(ref playback,null);
                    }
                }
            }
        }
    }
    public ValueTask DisposeAsync()
    { lock (disposalGate) return new(disposal ??= DisposeCore()); }
    private async Task DisposeCore()
    {
        try
        {
            CancelStartup(); await gate.WaitAsync().ConfigureAwait(false);
            try { disposed = true; await CloseOwned().ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        finally
        {
            try { if (observation is { } pending) await pending.ConfigureAwait(false); }
            finally
            {
                diagnosticsStop.Cancel();
                try { if (diagnosticSampling is { } sampling) await sampling.ConfigureAwait(false); }
                finally { diagnosticsStop.Dispose(); }
            }
        }
    }
}
