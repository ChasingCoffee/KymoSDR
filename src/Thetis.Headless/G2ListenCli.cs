using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Headless;

internal sealed record G2ListenOptions(G2RxCliOptions Receive,int? Device,bool Unmute,string? CapturePath,ReceiveMode Mode,
    int AudioGainDb,int AgcMaxGainDb);
internal sealed record G2ListenReport(bool Passed,bool HardwareContacted,bool TransmitAllowed,int FrequencyHz,
    string Antenna,long ElapsedMilliseconds,ReceiveState? Receive,PlaybackState? Output,G2ReceiveSafetyState? Safety,
    int CaptureFrames,DateTimeOffset? CaptureStartUtc,string? CaptureFormat,bool IdleAfterStop,string Mode,
    int AudioGainDb,int AgcMaxGainDb);

internal static class G2ListenCli
{
    private const string Help = """
        Usage: g2-listen --native-dir ABSOLUTE_PATH --nic LOCAL_ETHERNET_IPV4 --target G2_IPV4
          --mac MAC --confirm-ant1-rx [--frequency-hz 14000000..14350000] [--duration-seconds 5..60]
          [--device INDEX] [--unmute] [--capture ABSOLUTE_NEW_WAV] [--mode usb|lsb]
          [--af-db -60..0] [--agc-max-db 0..80]
        Uses the same G2 owner as the desktop. Default frequency 14.074 MHz USB, AF -40 dB.
        Defaults to muted no-device monitor. An enumerated physical device must be selected explicitly.
        Unmute waits for one second of produced RX audio. AF defaults -40 dB, AGC max 60 dB.
        Explicit gain overrides apply only when unmuting; startup stays muted at AF -40 dB or below.
        No microphone, CAT/PTT, keying or transmit.
        Capture is opt-in, requires a 60-second run, and retains at most 15 seconds of mono audio,
        starting near a UTC 15-second boundary. Writes normalized 48 kHz PCM WAV after shutdown.
        Normalization affects the offline file only, never speaker gain. Existing files are not overwritten.
        Exit: 0 pass, 2 syntax, 3 native unavailable, 4 run failure, 130 cancelled.
        """;
    internal static G2ListenOptions Parse(string[] args)
    {
        if (args.FirstOrDefault() != "g2-listen") throw new ArgumentException("Expected g2-listen.");
        var receive = new List<string> { "g2-receive" }; int? device = null; bool unmute = false; string? capture = null;
        var mode = ReceiveMode.Usb;
        int gain = -40,agcMax = 60;
        var seen = new HashSet<string>();
        for (int i = 1; i < args.Length; ++i)
        {
            string key = args[i];
            if (key is "--device" or "--unmute" or "--capture" or "--mode" or "--af-db" or "--agc-max-db")
            {
                if (!seen.Add(key)) throw new ArgumentException($"Repeated {key}.");
                if (key == "--unmute") { unmute = true; continue; }
                if (++i == args.Length) throw new ArgumentException($"Missing {key} value.");
                if (key is "--af-db" or "--agc-max-db")
                {
                    if (!int.TryParse(args[i],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int value) ||
                        (key == "--af-db" ? value is < -60 or > 0 : value is < 0 or > 80))
                        throw new ArgumentException("AF must be -60..0 dB and AGC maximum 0..80 dB.");
                    if (key == "--af-db") gain = value; else agcMax = value;
                }
                else if (key == "--mode") mode = args[i] switch { "usb" => ReceiveMode.Usb,"lsb" => ReceiveMode.Lsb,
                    _ => throw new ArgumentException("Mode must be usb or lsb.") };
                else if (key == "--capture")
                {
                    capture = args[i];
                    if (!Path.IsPathFullyQualified(capture) || !capture.EndsWith(".wav",StringComparison.OrdinalIgnoreCase) ||
                        File.Exists(capture) || !Directory.Exists(Path.GetDirectoryName(capture)))
                        throw new ArgumentException("Capture requires a new absolute .wav path in an existing directory.");
                }
                else if (int.TryParse(args[i],NumberStyles.None,CultureInfo.InvariantCulture,out int number) && number >= 0) device = number;
                else throw new ArgumentException("Device must be an enumerated nonnegative index.");
            }
            else receive.Add(key);
        }
        if (!receive.Contains("--frequency-hz")) receive.AddRange(["--frequency-hz","14074000"]);
        var options = G2ReceiveCli.Parse(receive.ToArray());
        if (capture is not null && options.DurationSeconds != 60) throw new ArgumentException("Aligned capture requires --duration-seconds 60.");
        return new(options,device,unmute,capture,mode,gain,agcMax);
    }
    internal static async Task<int> RunAsync(string[] args,TextWriter output,TextWriter error,CancellationToken token = default,
        Func<G2ListenOptions,CancellationToken,Task<G2ListenReport>>? runner = null)
    {
        if (args is ["g2-listen","--help"]) { output.WriteLine(Help); return 0; }
        G2ListenOptions options;
        try { options = Parse(args); }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested(); var result = await (runner ?? Listen)(options,token);
            output.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented = true,PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("G2 listening cancelled; owned output/receiver closed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Native libraries unavailable: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NetworkInformationException or SocketException or TimeoutException)
        { error.WriteLine($"G2 listening failed: {ex.Message}"); return 4; }
    }
    private static async Task<G2ListenReport> Listen(G2ListenOptions options,CancellationToken token)
    {
        var receive = options.Receive;
        PlaybackDevice? device = null;
        if (options.Device is { } index)
            device = (await Task.Run(() => PlaybackOutput.EnumerateDevices(receive.NativeDirectory),token))
                .SingleOrDefault(d => d.Index == index) ?? throw new ArgumentException("Refresh outputs and choose an available device.");
        var capture = options.CapturePath is null ? null : new ReceiveAudioCapture(15,
            DateTimeOffset.FromUnixTimeSeconds(((DateTimeOffset.UtcNow.ToUnixTimeSeconds()+24)/15)*15));
        await using var controller = new PreviewController();
        var clock = Stopwatch.StartNew();
        var request = new G2HardwareRequest(new(receive.LocalAddress,receive.RadioAddress,receive.MacAddress),
            receive.FrequencyHz,receive.DurationSeconds,true);
        await controller.ConnectG2Async(receive.NativeDirectory,request,device,token,
            initialSettings:new(FrequencyHz:receive.FrequencyHz,Mode:options.Mode,LowCutHz:100),capture:capture);
        bool unmuted = false;
        while (controller.Connected)
        {
            token.ThrowIfCancellationRequested();
            if (controller.Error is { } failure) throw new IOException("Receive playback failed.",failure);
            if (!unmuted && options.Unmute && controller.Snapshot?.Receive.AudioProduced >= 48000)
            {
                await controller.ApplyAsync(controller.Settings with { Muted = false,AudioGainDb = options.AudioGainDb,
                    AgcMaxGainDb = options.AgcMaxGainDb },token); unmuted = true;
            }
            if (clock.Elapsed.TotalSeconds > receive.DurationSeconds+20) throw new TimeoutException("Bounded G2 playback did not stop.");
            await Task.Delay(25,token);
        }
        if (controller.Error is { } terminalError) throw new IOException("Receive playback failed.",terminalError);
        var s = controller.LastSnapshot;
        bool passed = s is { Hardware.StopReason:1 } && s.Hardware.KeyBitsSeen == 0 && s.Hardware.PolicyRejections == 0 &&
            s.Hardware.StopDatagramsSent >= 3 && s.Receive.StatusPackets > 0 && s.Receive.IqPackets > 0 &&
            s.Receive.SocketErrors == 0 && s.Receive.DspErrors == 0 && s.Receive.MissingPackets == 0 &&
            s.Receive.LatePackets == 0 && s.Receive.MalformedPackets == 0 && s.Receive.ForeignPackets == 0 &&
            s.Receive.InputOverruns == 0 && s.Receive.AudioDropped == 0 && s.Output.Rejected == 0 &&
            s.Output.Underruns == 0 && s.Output.DriverUnderruns == 0 &&
            s.Output.Fault == PlaybackFault.None && s.Output.NonfiniteSamples == 0 && s.Output.ClippedSamples == 0 &&
            (!options.Unmute || unmuted);
        if (capture is not null)
        {
            capture.SaveWave(options.CapturePath!);
            passed &= capture.Frames == 15*48000;
        }
        // A successful UDP STOP send is not an acknowledgement. Recheck the
        // same idle identity after all owned streams have closed; never force stop.
        _ = await Task.Run(() => G2Preflight.Verify(request,token),token);
        return new(passed,true,false,receive.FrequencyHz,"ANT1",clock.ElapsedMilliseconds,s?.Receive,s?.Output,s?.Hardware,
            capture?.Frames ?? 0,capture?.FirstSampleObservedUtc,capture is null ? null : "Mono PCM16 48000 Hz; offline peak normalized to -6 dBFS; host UTC, not a radio timestamp.",true,options.Mode.ToString().ToUpperInvariant(),
            controller.Settings.AudioGainDb,controller.Settings.AgcMaxGainDb);
    }
}
