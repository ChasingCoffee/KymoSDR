using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace Thetis.Simulator;

internal static class SimulatorCli
{
    internal const string Help = """
        KymoSDR G2 simulator — synthetic Protocol 2 peer, IPv4 loopback ONLY.
        No hardware, native DSP or audio devices. Optional virtual TX sink; never RF.

        Usage:
          Thetis.Simulator selftest
          Thetis.Simulator tx-selftest
          Thetis.Simulator serve [options]
          Thetis.Simulator --help

        Options (serve only):
          --base-port N          Discovery/control base (default 51024; 0 = choose free layout).
                                 Uses ports within base..base+20; 1024 selects standard P2 ports.
          --tone-hz N            Synthetic RF tone frequency (default 14200000).
          --amplitude N          I/Q peak amplitude, 0..0.9 (default 0.25).
          --noise N              Seeded uniform noise peak, 0..0.09 (default 0).
          --seed N               Unsigned noise seed (default 1).
          --drop-every N         Drop every Nth I/Q packet per DDC (0 = off).
          --tx-mode off|sink     Opt in to virtual PTT/TX I/Q capture metrics (default off).
          --duration-seconds N   Stop after N seconds (default 300; 0 = until Ctrl-C).

        stdout is JSON Lines: ready/stopped events, or a selftest result.
        Custom layouts require explicit matching ports in the P2 general packet.
        Ten independent DDCs, 48/96/192/384 kHz, 24-bit I/Q. No synced DDCs,
        wideband, firmware programming, calibration or RF validation. TX sink:
        one 192 kHz/24-bit DUC, sample metrics only. No CW keyer, EER or PureSignal.
        Exit: 0 completed/help, 2 options, 3 socket error, 4 failed test, 130 Ctrl-C.
        """;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] || args is ["serve", "--help"])
            { output.WriteLine(Help); return 0; }
            if (args is ["selftest"])
            {
                output.WriteLine(JsonSerializer.Serialize(await SimulatorDiagnostics.RunAsync(token), Json));
                return 0;
            }
            if (args is ["tx-selftest"])
            {
                output.WriteLine(JsonSerializer.Serialize(await TransmitDiagnostics.RunAsync(token), Json));
                return 0;
            }
            var (options, duration) = Parse(args);
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (duration > 0) deadline.CancelAfter(TimeSpan.FromSeconds(duration));
            await using var simulator = G2Simulator.Open(options, deadline.Token);
            output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, eventType = "ready", simulator = true,
                name = "KymoSDR G2 simulator", address = "127.0.0.1", basePort = simulator.BasePort,
                ddc0Port = simulator.BasePort + 11, ddcCount = 10, toneHz = options.ToneFrequencyHz,
                transmitSupported = options.SimulateTransmit, transmitMode = options.SimulateTransmit ? "sink" : "off",
                hardwareTransmitSupported = false }, Json));
            output.Flush();
            await simulator.Completion;
            output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, eventType = "stopped", state = simulator.State }, Json));
            token.ThrowIfCancellationRequested();
            return 0;
        }
        catch (OperationCanceledException) { error.WriteLine("Simulator cancelled; sockets and worker released."); return 130; }
        catch (ArgumentException ex) { error.WriteLine(ex.Message); return 2; }
        catch (SocketException ex) { error.WriteLine($"Simulator socket error: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException)
        { error.WriteLine($"Simulator failed: {ex.Message}"); return 4; }
    }

    internal static (SimulatorOptions Options, int Duration) Parse(string[] args)
    {
        if (args.Length == 0 || args[0] != "serve" || args.Length % 2 != 1) throw new ArgumentException("Use serve [options], selftest, tx-selftest, or --help.");
        var options = new SimulatorOptions();
        int duration = 300;
        var seen = new HashSet<string>();
        for (int i = 1; i < args.Length; i += 2)
        {
            string key = args[i], value = args[i + 1];
            if (!seen.Add(key)) throw new ArgumentException($"Duplicate option {key}.");
            switch (key)
            {
                case "--base-port": options = options with { BasePort = Integer(value) }; break;
                case "--tone-hz": options = options with { ToneFrequencyHz = Number(value) }; break;
                case "--amplitude": options = options with { Amplitude = Number(value) }; break;
                case "--noise": options = options with { NoiseAmplitude = Number(value) }; break;
                case "--drop-every": options = options with { DropEvery = Integer(value) }; break;
                case "--duration-seconds": duration = Integer(value); break;
                case "--tx-mode": options = options with { SimulateTransmit = value switch
                    { "off" => false, "sink" => true, _ => throw new ArgumentException("TX mode must be off or sink. Hardware TX is unavailable.") } }; break;
                case "--seed":
                    if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint seed)) throw new ArgumentException("Invalid unsigned seed.");
                    options = options with { Seed = seed }; break;
                default: throw new ArgumentException($"Unknown option {key}. There is no LAN bind or hardware transmit option.");
            }
        }
        options.Validate();
        if (duration is < 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(duration));
        return (options, duration);
    }
    private static int Integer(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        ? value : throw new ArgumentException($"Invalid nonnegative integer: {text}.");
    private static double Number(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value : throw new ArgumentException($"Invalid number: {text}.");
}
