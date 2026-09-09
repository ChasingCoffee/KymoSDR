using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Headless;

internal sealed record G2SoakOptions(G2ListenOptions Listen,int DurationSeconds,int Reconnects,string ReportPath,int FirstChannel)
{
    internal G2HardwareRequest Request(int seconds) => new(new(Listen.Receive.LocalAddress,Listen.Receive.RadioAddress,
        Listen.Receive.MacAddress),Listen.Receive.FrequencyHz,seconds,true,ConfirmExtendedReceive:seconds > 60);
}
internal sealed record G2SoakObservation(ReceiveState Receive,PlaybackState Output,G2ReceiveSafetyState? Safety,
    long SpectrumSequence,bool FiniteSpectrum);
internal sealed record G2SoakPhase(int Index,int RequestedSeconds,double ElapsedSeconds,bool Passed,
    bool? IdleAfterStop,bool Unmuted,long ObservedSpectrumFrames,G2SoakObservation? LastObserved);
internal sealed record G2SoakReport(int SchemaVersion,bool Passed,string Outcome,string? FailureType,string? CleanupFailureType,
    bool HardwareAttempted,bool TransmitAllowed,int RequestedSeconds,int RequestedReconnects,double ElapsedSeconds,
    string Qualification,G2SoakPhase[] Phases,DiagnosticReport? Diagnostics);

// The production adapter owns the same controller/pump as the desktop. Tests
// substitute this boundary and a virtual monotonic clock, never a LAN peer.
internal interface IG2SoakSession : IAsyncDisposable
{
    Task ConnectAsync(G2SoakOptions options,int seconds,CancellationToken token);
    bool Connected { get; }
    Exception? Error { get; }
    G2SoakObservation? Observation { get; }
    DiagnosticReport Diagnostics { get; }
    Task UnmuteAsync(G2SoakOptions options,CancellationToken token);
}
internal sealed class G2SoakServices
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    internal Func<IG2SoakSession> CreateSession { get; init; } = () => new G2SoakSession();
    internal Func<G2HardwareRequest,CancellationToken,Task> VerifyIdle { get; init; } =
        (request,token) => Task.Run(() => G2Preflight.Verify(request,token),token);
    internal Func<TimeSpan,CancellationToken,Task> Delay { get; init; } = Task.Delay;
    internal Func<double>? ReadSeconds { get; init; }
    internal double Seconds => ReadSeconds?.Invoke() ?? clock.Elapsed.TotalSeconds;
    internal bool Hardware { get; init; } = true;
}
internal sealed class G2SoakSession : IG2SoakSession
{
    private readonly PreviewController controller = new();
    private AudioOutputPreferences? bookmark;
    public bool Connected => controller.Connected;
    public Exception? Error => controller.Error;
    public DiagnosticReport Diagnostics => controller.Diagnostics.Snapshot();
    public G2SoakObservation? Observation
    {
        get
        {
            var s = controller.Snapshot ?? controller.LastSnapshot;
            if (s is null) return null;
            bool finite = true;
            if (s.Spectrum is { } spectrum)
                foreach (float value in spectrum.LevelsDb.Span) finite &= float.IsFinite(value);
            return new(s.Receive,s.Output,s.Hardware,s.Spectrum?.Sequence ?? 0,finite);
        }
    }
    public async Task ConnectAsync(G2SoakOptions options,int seconds,CancellationToken token)
    {
        var listen = options.Listen; PlaybackDevice? device = null;
        if (listen.Device is { } index)
        {
            var devices = await Task.Run(() => PlaybackOutput.EnumerateDevices(listen.Receive.NativeDirectory),token);
            if (bookmark is not null)
                device = bookmark.Match(devices) ?? throw new IOException("Selected output disappeared or changed; no fallback.");
            else
            {
                device = devices.SingleOrDefault(d => d.Index == index) ?? throw new ArgumentException("Choose an enumerated output index.");
                var pair = device.Pairs.SingleOrDefault(p => p.FirstChannel == options.FirstChannel)
                    ?? throw new ArgumentException("Choose an available stereo pair.");
                device = device.WithPair(pair); bookmark = AudioOutputPreferences.From(device,pair);
            }
        }
        await controller.ConnectG2Async(listen.Receive.NativeDirectory,options.Request(seconds),device,token,
            initialSettings:new(FrequencyHz:listen.Receive.FrequencyHz,Mode:listen.Mode,LowCutHz:100));
    }
    public Task UnmuteAsync(G2SoakOptions options,CancellationToken token) => controller.ApplyAsync(controller.Settings with
        { Muted = false,AudioGainDb = options.Listen.AudioGainDb,AgcMaxGainDb = options.Listen.AgcMaxGainDb },token);
    public ValueTask DisposeAsync() => controller.DisposeAsync();
}

internal static class G2SoakCli
{
    private const string Help = """
        Usage: g2-soak --native-dir ABSOLUTE_PATH --nic LOCAL_ETHERNET_IPV4 --target G2_IPV4
          --mac MAC --confirm-ant1-rx --confirm-extended-rx --duration-seconds 60..3600
          --report ABSOLUTE_NEW_JSON [--reconnects 0..10]
          [--frequency-hz 14000000..14350000] [--mode usb|lsb]
          [--device INDEX --first-channel EVEN_ZERO_BASED_CHANNEL] [--unmute]
          [--af-db -60..0] [--agc-max-db 0..80]
        Explicit hardware RX qualification; no TX, microphone or sample recording.
        One bounded long session, then the explicitly requested number of 10-second reconnects.
        Fresh idle identity/explicit Ethernet preflight every connection; no takeover, retries or fallback.
        Defaults: 14.074 MHz USB, no reconnects, muted no-device monitor, AF -40 dB/AGC max 60 dB.
        --unmute explicitly permits listening in EACH requested phase, after one second of RX PCM.
        Native hard deadline, ANT1-only packet policy, key/PTT and IQ/status watchdogs stay enabled.
        PA-disable is requested, not a physical RF interlock. Stay present for initial qualification.
        Ctrl-C closes owned streams; success/failure/cancellation retain aggregate diagnostics.
        Reports exclude addresses, device names and waveform data. Existing reports are never overwritten.
        No-device output cannot qualify physical playback; no desktop rendering is measured here.
        Exit: 0 pass, 2 syntax, 3 native unavailable, 4 run/report failure, 130 cancelled.
        """;

    internal static G2SoakOptions Parse(string[] args)
    {
        if (args.FirstOrDefault() != "g2-soak") throw new ArgumentException("Expected g2-soak.");
        var listen = new List<string> { "g2-listen" };
        var values = new Dictionary<string,string>(); bool confirmed = false;
        for (int i = 1; i < args.Length; ++i)
        {
            string key = args[i];
            if (key == "--confirm-extended-rx")
            {
                if (confirmed) throw new ArgumentException("Repeated extended receive confirmation.");
                confirmed = true;
            }
            else if (key is "--duration-seconds" or "--reconnects" or "--report" or "--first-channel")
            {
                if (++i == args.Length || args[i].StartsWith("--",StringComparison.Ordinal) || !values.TryAdd(key,args[i]))
                    throw new ArgumentException($"Missing or repeated {key}.");
            }
            else if (key == "--capture") throw new ArgumentException("Endurance reports do not record audio.");
            else listen.Add(key);
        }
        int Number(string key,int fallback) => !values.TryGetValue(key,out string? text) ? fallback :
            int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int number) ? number :
                throw new ArgumentException($"Invalid {key}.");
        int seconds = Number("--duration-seconds",0),reconnects = Number("--reconnects",0),channel = Number("--first-channel",0);
        if (!confirmed || seconds is < 60 or > 3600 || reconnects is < 0 or > 10)
            throw new ArgumentException("Explicit --confirm-extended-rx and duration 60..3600 seconds required; reconnects 0..10.");
        if (!values.TryGetValue("--report",out string? report) || !Path.IsPathFullyQualified(report) ||
            !report.EndsWith(".json",StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(report)) ||
            File.Exists(report) || Directory.Exists(report))
            throw new ArgumentException("Report must be a new absolute .json path in an existing directory.");
        // Validate the unchanged short-listen grammar; only this new command
        // carries the separately confirmed extended duration into the request.
        var options = G2ListenCli.Parse([..listen,"--duration-seconds","60"]);
        if (channel is < 0 or > 126 || channel % 2 != 0 || (values.ContainsKey("--first-channel") && options.Device is null))
            throw new ArgumentException("First channel must be even, 0..126, and requires an explicit device.");
        return new(options,seconds,reconnects,report,channel);
    }

    internal static async Task<int> RunAsync(string[] args,TextWriter output,TextWriter error,CancellationToken token = default,
        G2SoakServices? services = null)
    {
        if (args is ["g2-soak","--help"]) { output.WriteLine(Help); return 0; }
        G2SoakOptions options;
        try { options = Parse(args); }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Help); return 2; }
        try
        {
            // Reserve the exact new report BEFORE any device enumeration or
            // network activity. A concurrent creator is never overwritten.
            await using var file = new FileStream(options.ReportPath,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            var report = await RunCampaign(options,services ?? new(),error,token);
            var json = new JsonSerializerOptions(AtomicJsonFile.Options)
                { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
            await JsonSerializer.SerializeAsync(file,report,json,CancellationToken.None);
            await file.FlushAsync(CancellationToken.None); file.Flush(flushToDisk:true);
            output.WriteLine(JsonSerializer.Serialize(new { report.Passed,report.Outcome,report.FailureType,
                report.CleanupFailureType,report.ElapsedSeconds,CompletedPhases = report.Phases.Count(p => p.Passed) },json));
            return report.Passed ? 0 : report.CleanupFailureType is not null ? 4 : report.Outcome == "cancelled" ? 130 :
                report.Outcome == "native-unavailable" ? 3 : 4;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { error.WriteLine($"G2 endurance report unavailable: {ex.Message}"); return 4; }
    }

    private static async Task<G2SoakReport> RunCampaign(G2SoakOptions options,G2SoakServices services,TextWriter progress,CancellationToken token)
    {
        double start = services.Seconds;
        var phases = new List<G2SoakPhase>(); IG2SoakSession? session = null;
        Exception? failure = null,cleanup = null; bool attempted = false;
        try
        {
            token.ThrowIfCancellationRequested(); session = services.CreateSession();
            for (int phase = 0; phase <= options.Reconnects; ++phase)
            {
                token.ThrowIfCancellationRequested();
                int seconds = phase == 0 ? options.DurationSeconds : 10;
                double phaseStart = services.Seconds,lastAudioAt = phaseStart,lastSpectrumAt = phaseStart,nextProgress = phaseStart+10;
                long lastAudio = 0,lastSpectrum = 0,spectra = 0;
                bool unmuted = false,passed = false; bool? idle = null;
                G2SoakObservation? observed = null;
                try
                {
                    attempted = true;
                    progress.WriteLine($"G2 RX phase {phase+1}/{options.Reconnects+1}: {seconds}s; TX disabled.");
                    await session.ConnectAsync(options,seconds,token);
                    // Startup is outside the observed streaming-duration gate.
                    double connectedAt = services.Seconds; lastAudioAt = lastSpectrumAt = connectedAt;
                    while (session.Connected)
                    {
                        token.ThrowIfCancellationRequested();
                        if (session.Error is { } error) throw new IOException("Receive/output failed.",error);
                        observed = session.Observation;
                        if (observed is { } s)
                        {
                            RequireClean(s);
                            if (s.Output.Rendered > lastAudio) { lastAudio = s.Output.Rendered; lastAudioAt = services.Seconds; }
                            if (s.SpectrumSequence > lastSpectrum) { lastSpectrum = s.SpectrumSequence; ++spectra; lastSpectrumAt = services.Seconds; }
                            if (options.Listen.Unmute && !unmuted && s.Receive.AudioProduced >= 48000 && s.Safety?.WorkerRunning == true)
                            { await session.UnmuteAsync(options,token); unmuted = true; }
                        }
                        if (services.Seconds-lastAudioAt > 10 || services.Seconds-lastSpectrumAt > 10)
                            throw new TimeoutException("Receive PCM or spectrum stopped advancing for ten seconds.");
                        if (services.Seconds-connectedAt > seconds+15) throw new TimeoutException("Native receive deadline did not close the session.");
                        if (services.Seconds >= nextProgress)
                        {
                            progress.WriteLine($"Phase {phase+1}: {services.Seconds-connectedAt:F0}s, observed spectra {spectra}, output frames {lastAudio}.");
                            nextProgress = services.Seconds+10;
                        }
                        await services.Delay(TimeSpan.FromMilliseconds(100),token);
                    }
                    token.ThrowIfCancellationRequested();
                    observed = session.Observation ?? observed;
                    if (session.Error is { } terminal) throw new IOException("Receive/output failed at shutdown.",terminal);
                    if (observed is null) throw new IOException("No terminal receive observation.");
                    RequireClean(observed);
                    if (observed.Safety is not { WorkerRunning:false,StopReason:1,StopDatagramsSent:>=3 } ||
                        observed.Receive.AudioProduced < 48000 || observed.Output.Rendered < 48000 || spectra < 2 ||
                        observed.Receive.IqPackets == 0 || observed.Receive.StatusPackets == 0 ||
                        services.Seconds-connectedAt < seconds-2 || (options.Listen.Unmute && !unmuted))
                        throw new IOException("Receive phase ended without its duration, progress and native deadline gates.");
                    // STOP send success is not an acknowledgement. Same identity
                    // must be idle after the controller has joined all owners.
                    await services.VerifyIdle(options.Request(seconds),token); idle = true; passed = true;
                }
                finally
                {
                    phases.Add(new(phase,seconds,services.Seconds-phaseStart,passed,idle,unmuted,spectra,observed));
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (session is not null)
                try { await session.DisposeAsync(); }
                catch (Exception ex) { cleanup = ex; }
        }
        DiagnosticReport? diagnostics = null;
        if (session is not null)
            try { diagnostics = session.Diagnostics; }
            catch (Exception ex) { cleanup ??= ex; }
        bool success = failure is null && cleanup is null && phases.Count == options.Reconnects+1 && phases.All(p => p.Passed);
        string outcome = success ? "passed" : failure is OperationCanceledException ? "cancelled" :
            failure is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException
                ? "native-unavailable" : "failed";
        return new(1,success,outcome,failure?.GetType().Name,cleanup?.GetType().Name,services.Hardware && attempted,false,
            options.DurationSeconds,options.Reconnects,services.Seconds-start,
            "ANT1 RX only; no TX or waveform recording. One continuous bounded phase plus optional 10-second reconnects. " +
            "Network loss and application overruns are separate counters; both must be zero. " +
            "No-device output is sample-driven, not physical audio qualification. Observed spectrum frames are polled/coalesced, " +
            "not rendered frames; this headless command does not qualify desktop cadence or sleep/wake. " +
            "CPU/memory are measurements, not calibrated performance budgets. Cancellation/failure never retries or reconnects. " +
            "IdleAfterStop is null when not confirmed. SIGKILL/power loss cannot guarantee a completed report.",
            phases.ToArray(),diagnostics);
    }

    internal static void RequireClean(G2SoakObservation s)
    {
        var r = s.Receive; var o = s.Output;
        if (!s.FiniteSpectrum || s.Safety is not { KeyBitsSeen:0,PolicyRejections:0 } ||
            (s.Safety.StopReason != 0 && s.Safety.StopReason != 1) ||
            r.MissingPackets != 0 || r.LatePackets != 0 || r.MalformedPackets != 0 || r.ForeignPackets != 0 ||
            r.SocketErrors != 0 || r.DspErrors != 0 || r.InputOverruns != 0 || r.AudioDropped != 0 ||
            o.Underruns != 0 || o.DriverUnderruns != 0 || o.Rejected != 0 || o.NonfiniteSamples != 0 || o.ClippedSamples != 0 ||
            o.Fault != PlaybackFault.None || o.Switching || o.SwitchDiscardedFrames != 0)
            throw new IOException("G2 receive safety, network or output counters failed; see retained diagnostics.");
    }
}
