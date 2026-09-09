using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Headless;

internal sealed record G2RxCliOptions(string NativeDirectory, string LocalAddress, string RadioAddress,
    string MacAddress, int FrequencyHz, int DurationSeconds);

internal sealed record G2RxReport(int SchemaVersion, bool Passed, bool HardwareContacted, bool TransmitAllowed,
    string Interface, string LocalAddress, string RadioAddress, string MacAddress,
    int FirmwareCode, int BetaByte, int ProtocolByte, int ReceiverCount,
    string Antenna, string Mode, int FrequencyHz, int InputRate, int Ddc, int Adc,
    long ElapsedMilliseconds, long AudioFrames, double AudioRms, double AudioPeak,
    long SpectrumFrames, double? SpectrumPeakDb, double? SpectrumPeakHz,
    ReceiveState Native, G2ReceiveSafetyState Safety, bool IdleAfterStop);

internal static class G2ReceiveCli
{
    private const string Help = """
        Usage: Thetis.Headless g2-receive --native-dir ABSOLUTE_PATH --nic LOCAL_IPV4
          --target G2_IPV4 --mac XX:XX:XX:XX:XX:XX --confirm-ant1-rx
          [--frequency-hz 14000000..14350000] [--duration-seconds 5..60]
        Explicit Ethernet only, idle Saturn/P2 identity checked before start. No Wi-Fi fallback.
        ANT1/ADC0/DDC2/192k, USB 300..3000 Hz, ADC attenuation 10 dB, AF -40 dB/medium AGC.
        Defaults: 14200000 Hz, 10 seconds. Aggregate JSON only; no sound or sample recording.
        No PTT/CW/TX samples. PA-disable requested; not a physical RF interlock.
        Stops on native deadline, Ctrl-C, IQ/status timeout or reported PTT/key inputs.
        Exit: 0 pass, 2 arguments, 3 native unavailable, 4 preflight/receive failure, 130 cancelled.
        """;

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default, Func<G2RxCliOptions, CancellationToken, Task<G2RxReport>>? runner = null)
    {
        if (args is ["g2-receive", "--help"]) { output.WriteLine(Help); return 0; }
        G2RxCliOptions options;
        try { options = Parse(args); }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? RunHardwareAsync)(options, token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
                { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("G2 RX cancelled; receive owner disposed and STOP attempted."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or NotSupportedException)
        { error.WriteLine($"Compatible native G2 RX library required: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or SocketException or NetworkInformationException or TimeoutException)
        { error.WriteLine($"G2 RX failed: {ex.Message}"); return 4; }
    }

    internal static G2RxCliOptions Parse(string[] args)
    {
        if (args.FirstOrDefault() != "g2-receive") throw new ArgumentException("Expected g2-receive.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool confirmed = false;
        for (int i = 1; i < args.Length; ++i)
        {
            string name = args[i];
            if (name == "--confirm-ant1-rx" && !confirmed) { confirmed = true; continue; }
            if (name is not ("--native-dir" or "--nic" or "--target" or "--mac" or "--frequency-hz" or "--duration-seconds") ||
                ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(name, args[i]))
                throw new ArgumentException("Unknown, repeated or incomplete G2 RX option.");
        }
        string Required(string key) => values.TryGetValue(key, out string? value) ? value : throw new ArgumentException($"Missing {key}.");
        int Number(string key, int fallback) => !values.TryGetValue(key, out var text) ? fallback :
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : throw new ArgumentException($"Invalid {key}.");
        if (!confirmed) throw new ArgumentException("Explicit --confirm-ant1-rx is required.");
        string directory = Required("--native-dir");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Native directory must be absolute.");
        string mac = NormalizeMac(Required("--mac"));
        int frequency = Number("--frequency-hz", 14_200_000), seconds = Number("--duration-seconds", 10);
        // Endpoint syntax now; actual interface subnet checked after enumeration.
        string local = Required("--nic"), radio = Required("--target");
        _ = IPAddress.TryParse(local, out var localIp); _ = IPAddress.TryParse(radio, out var radioIp);
        if (localIp is null || radioIp is null || localIp.AddressFamily != AddressFamily.InterNetwork ||
            radioIp.AddressFamily != AddressFamily.InterNetwork || localIp.ToString() != local || radioIp.ToString() != radio ||
            IPAddress.IsLoopback(localIp) || IPAddress.IsLoopback(radioIp)) throw new ArgumentException("Literal non-loopback IPv4 endpoints required.");
        if (seconds is < 5 or > 60 || frequency is < 14_000_000 or > 14_350_000)
            throw new ArgumentException("Duration must be 5..60 seconds and frequency 14.000..14.350 MHz.");
        if (local == radio || localIp.GetAddressBytes()[0] is 0 or >= 224 || radioIp.GetAddressBytes()[0] is 0 or >= 224)
            throw new ArgumentException("Distinct unicast endpoints required.");
        return new(directory, local, radio, mac, frequency, seconds);
    }

    internal static string NormalizeMac(string text)
        => G2Preflight.NormalizeMac(text);
    private static G2HardwareRequest Request(G2RxCliOptions options) => new(
        new(options.LocalAddress,options.RadioAddress,options.MacAddress),options.FrequencyHz,options.DurationSeconds,true);

    internal static NicRadioScanResult SelectInterface(List<NicRadioScanResult> interfaces, G2RxCliOptions options)
        => G2Preflight.SelectInterface(interfaces,Request(options));

    internal static RadioInfo SelectRadio(List<NicRadioScanResult> scan, G2RxCliOptions options, bool requireIdle)
        => G2Preflight.SelectRadio(scan,Request(options),requireIdle);

    private static Task<G2RxReport> RunHardwareAsync(G2RxCliOptions options, CancellationToken token)
    {
        var backend = new DiscoveryBackend();
        var scanOptions = new RadioDiscoveryOptions { FixedLocalIp = IPAddress.Parse(options.LocalAddress),
            FixedTargetIp = IPAddress.Parse(options.RadioAddress), ProtocolMode = RadioDiscoveryProtocolMode.P2Only,
            MaxScanMilliseconds = 2000, IncludeGeneralBroadcast = false };
        var nic = SelectInterface(backend.List(scanOptions), options); // validate subnet before any traffic
        var radio = SelectRadio(backend.Discover(scanOptions, token), options, true);
        var hardware = new G2ReceiveOptions(options.RadioAddress, options.LocalAddress, nic.LocalMaskIPv4.ToString(),
            options.FrequencyHz, options.DurationSeconds);
        var clock = Stopwatch.StartNew();
        long frames = 0, spectra = 0; double energy = 0, peak = 0;
        double? spectrumPeak = null, spectrumHz = null;
        ReceiveState native; G2ReceiveSafetyState safety;
        using (var session = G2ReceiveSession.Open(options.NativeDirectory, hardware, token))
        {
            double[] audio = new double[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int count = session.ReadAudio(audio);
                for (int i = 0; i < count * 2; ++i)
                {
                    if (!double.IsFinite(audio[i])) throw new InvalidDataException("Non-finite RX audio.");
                    energy += audio[i] * audio[i]; peak = Math.Max(peak, Math.Abs(audio[i]));
                }
                frames += count;
                var spectrum = session.ReadSpectrum();
                if (spectrum is not null)
                {
                    ++spectra;
                    var levels = spectrum.LevelsDb.Span; int maximum = 0;
                    for (int i = 0; i < levels.Length; ++i)
                    {
                        if (!float.IsFinite(levels[i])) throw new InvalidDataException("Non-finite RX spectrum.");
                        if (levels[i] > levels[maximum]) maximum = i;
                    }
                    spectrumPeak = levels[maximum]; spectrumHz = spectrum.FrequencyAt(maximum);
                }
                safety = session.Safety;
                if (!safety.WorkerRunning) break;
                if (clock.Elapsed.TotalSeconds > options.DurationSeconds + 15) throw new TimeoutException("Native receive deadline did not complete.");
                Thread.Sleep(count == 0 ? 2 : 1);
            }
            native = session.State;
        } // joined native owner before checking idle; no session takeover/forced remote stop
        var stopped = SelectRadio(backend.Discover(scanOptions, token), options, false);
        bool passed = safety.StopReason == 1 && safety.KeyBitsSeen == 0 && safety.PolicyRejections == 0 &&
            safety.StopDatagramsSent >= 3 && !stopped.IsBusy && frames > 48000 && spectra >= 2 && energy > 0 &&
            native.IqPackets > 0 && native.StatusPackets > 0 && native.SocketErrors == 0 && native.DspErrors == 0 &&
            native.InputOverruns == 0 && native.AudioDropped == 0 && native.MalformedPackets == 0 &&
            native.MissingPackets == 0 && native.LatePackets == 0;
        return Task.FromResult(new G2RxReport(1, passed, true, false, nic.NicName, options.LocalAddress, options.RadioAddress,
            options.MacAddress, radio.CodeVersion, radio.BetaVersion, radio.Protocol2Supported, radio.NumRxs,
            "ANT1", "USB", options.FrequencyHz, 192000, 2, 0, clock.ElapsedMilliseconds, frames,
            frames == 0 ? 0 : Math.Sqrt(energy / (2 * frames)), peak, spectra, spectrumPeak, spectrumHz, native, safety, !stopped.IsBusy));
    }
}
