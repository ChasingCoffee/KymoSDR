using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Thetis.Headless;

internal interface IDiscoveryBackend
{
    List<NicRadioScanResult> List(RadioDiscoveryOptions options);
    List<NicRadioScanResult> Discover(RadioDiscoveryOptions options, CancellationToken cancellation);
}

internal sealed class DiscoveryBackend : IDiscoveryBackend
{
    private readonly RadioDiscoveryService service = new();

    public List<NicRadioScanResult> List(RadioDiscoveryOptions options) => service.ListUsableNics(options);
    public List<NicRadioScanResult> Discover(RadioDiscoveryOptions options, CancellationToken cancellation) =>
        service.DiscoverUsingAllNics(options, cancellation);
}

internal static class DiscoveryCli
{
    internal const string Help = """
        Thetis.Headless — discovery and offline DSP tests (does not start RX or TX on a radio)

        Commands:
          nics       List usable IPv4 interfaces. Sends no discovery packets.
          discover   Send HPSDR discovery requests; finish after the scan/deadline.
          dsp-selftest --native-dir ABSOLUTE_PATH
                     Run offline DSP checks; JSON output. No radio or audio devices.
          session-selftest --native-dir ABSOLUTE_PATH
                     Run 100 offline ChannelMaster lifecycles. No network or devices.
          --help     Show this help. No command also shows help.

        Options:
          --nic IPv4              Bind to this local interface address.
          --protocol p1|p2|both    Protocol selection (default: both).
          --target IPv4           Filter to this radio; P2-only scans use unicast.
          --port 1..65535         Radio discovery port (default: 1024).
          --local-port 0..65535   Local UDP bind port (default: 0 / ephemeral).
          --timeout-ms 1..60000   Whole-scan deadline (default: 3000 ms).
          --profile ultra-fast|very-fast|fast|balanced|safe|very-tolerant
                                 Retry/quiet timing (default: balanced).
          --allow-loopback        Include loopback for a local simulator.
          --include-other        Include non-Ethernet/WiFi interfaces (e.g. VM NICs).
          --ignore-subnet         Accept replies outside the selected NIC subnet.
          --no-general-broadcast  Omit 255.255.255.255 (useful for loopback tests).
          --json                  Write one schema-versioned JSON result to stdout.

        P1/both scans may broadcast even with --target; the target filters replies.
        Deadline results may be partial; inspect deadlineReached and diagnostics.
        Exit codes: 0 success/help, 1 no radio, 2 invalid arguments,
                    3 network/no eligible interface, 4 unexpected error, 130 cancelled.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static int Run(string[] args, TextWriter output, TextWriter error,
        IDiscoveryBackend backend, CancellationToken cancellation = default)
    {
        var timer = Stopwatch.StartNew();
        bool json = args.Contains("--json", StringComparer.Ordinal);
        string command = args.FirstOrDefault() ?? "help";
        try
        {
            CliOptions cli = Parse(args);
            command = cli.Command;
            if (command == "help")
            {
                if (json)
                    output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, command, status = "ok", exitCode = 0, help = Help }, JsonOptions));
                else
                    output.WriteLine(Help);
                return 0;
            }

            cancellation.ThrowIfCancellationRequested();
            List<NicRadioScanResult> results;
            if (command == "nics")
            {
                results = backend.List(cli.Options);
                if (cli.Options.FixedLocalIp is not null)
                    results = results.Where(n => cli.Options.FixedLocalIp.Equals(n.LocalIPv4)).ToList();
            }
            else
            {
                results = backend.Discover(cli.Options, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();

            bool networkError = results.Count == 0 || results.Any(n => n.Diagnostics?.SocketError == true);
            int radioCount = results.Sum(n => n.Radios.Count);
            int code = networkError ? 3 : command == "discover" && radioCount == 0 ? 1 : 0;
            string status = code switch { 3 => "networkError", 1 => "noRadio", _ => "ok" };
            string? message = results.Count == 0 ? "No eligible IPv4 interface matches the requested options." :
                networkError ? "One or more interfaces failed; inspect per-interface diagnostics. Results may be partial." : null;
            var report = new DiscoveryReport(1, command, status, code, timer.ElapsedMilliseconds,
                results.Any(n => n.Diagnostics?.DeadlineReached == true), results.Select(ToInterface).ToArray(), message);
            WriteReport(report, json, output, error);
            return code;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Failure("cancelled", 130, "Discovery cancelled; sockets have been released.");
        }
        catch (ArgumentException ex)
        {
            return Failure("invalidArguments", 2, ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            return Failure("networkError", 3, ex.Message);
        }
        catch (Exception ex)
        {
            return Failure("error", 4, $"{ex.GetType().Name}: {ex.Message}");
        }

        int Failure(string status, int code, string message)
        {
            WriteReport(new DiscoveryReport(1, command, status, code, timer.ElapsedMilliseconds, false, [], message), json, output, error);
            return code;
        }
    }

    internal static CliOptions Parse(string[] args)
    {
        if (args.Length == 0) return new("help", new());
        if (args[0] is "--help" or "-h" or "help")
        {
            if (args.Skip(1).Any(a => a != "--json")) throw new ArgumentException("Unexpected argument after help.");
            return new("help", new());
        }
        string command = args[0];
        if (command is not ("nics" or "discover")) throw new ArgumentException($"Unknown command '{command}'. Use --help.");
        var options = new RadioDiscoveryOptions { MaxScanMilliseconds = 3000 };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option)) throw new ArgumentException($"Repeated option '{option}'.");
            switch (option)
            {
                case "--json": break;
                case "--allow-loopback": options.AllowLoopback = true; break;
                case "--include-other": options.IncludeOtherInterfaceTypes = true; break;
                case "--ignore-subnet": options.IgnoreSubnetCheck = true; break;
                case "--no-general-broadcast": options.IncludeGeneralBroadcast = false; break;
                case "--nic": options.FixedLocalIp = IPv4(Value()); break;
                case "--target": options.FixedTargetIp = IPv4(Value()); break;
                case "--port": options.DiscoveryPortBase = Number(Value(), 1, 65535); break;
                case "--local-port": options.BindLocalPort = Number(Value(), 0, 65535); break;
                case "--timeout-ms": options.MaxScanMilliseconds = Number(Value(), 1, 60000); break;
                case "--protocol":
                    options.ProtocolMode = Value() switch
                    {
                        "p1" => RadioDiscoveryProtocolMode.P1Only,
                        "p2" => RadioDiscoveryProtocolMode.P2Only,
                        "both" => RadioDiscoveryProtocolMode.Auto,
                        _ => throw new ArgumentException("Protocol must be p1, p2 or both.")
                    };
                    break;
                case "--profile":
                    options.ScanPerformance = Value() switch
                    {
                        "ultra-fast" => ScanPerformanceProfile.UltraFast,
                        "very-fast" => ScanPerformanceProfile.VeryFast,
                        "fast" => ScanPerformanceProfile.Fast,
                        "balanced" => ScanPerformanceProfile.Balanced,
                        "safe" => ScanPerformanceProfile.Safe,
                        "very-tolerant" => ScanPerformanceProfile.VeryTolerant,
                        _ => throw new ArgumentException("Unknown scan profile. Use --help.")
                    };
                    break;
                default: throw new ArgumentException($"Unknown option '{option}'. Use --help.");
            }

            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for '{option}'.");
                return args[i];
            }
        }
        return new(command, options);
    }

    private static IPAddress IPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
            throw new ArgumentException($"Expected a unicast IPv4 address, got '{value}'.");
        return address;
    }

    private static int Number(string value, int min, int max)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < min || parsed > max)
            throw new ArgumentException($"Expected an integer from {min} to {max}, got '{value}'.");
        return parsed;
    }

    private static InterfaceReport ToInterface(NicRadioScanResult nic) => new(
        nic.NicName, nic.NicId, nic.NicDescription, nic.LocalIPv4?.ToString(), nic.LocalMaskIPv4?.ToString(),
        nic.NicInterfaceTypeString, nic.IsLoopbackLocal, nic.IsApipaLocal, nic.Diagnostics,
        nic.Radios.Select(r => new RadioReport(r.Protocol.ToString(), r.IpAddress?.ToString(), r.MacAddress,
            Enum.IsDefined(r.DeviceType) ? r.DeviceType.ToString() : "Unknown", (int)r.DeviceType,
            r.CodeVersion, r.BetaVersion, r.Protocol2Supported, r.NumRxs, r.IsBusy,
            r.DiscoveryPortBase, r.PortCount)).ToArray());

    private static void WriteReport(DiscoveryReport report, bool json, TextWriter output, TextWriter error)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return;
        }
        output.WriteLine($"{report.Command}: {report.Status} ({report.DurationMilliseconds} ms)");
        foreach (var nic in report.Interfaces)
        {
            output.WriteLine($"  {nic.Name}: {nic.LocalAddress}/{nic.SubnetMask} [{nic.InterfaceType}]");
            foreach (var radio in nic.Radios)
                output.WriteLine($"    {radio.Protocol} {radio.DeviceType} {radio.Address}:{radio.DiscoveryPort} " +
                    $"MAC={radio.MacAddress} firmwareCode={radio.FirmwareCode} beta={radio.BetaVersion} " +
                    $"receivers={radio.ReceiverCount} busy={radio.IsBusy}");
            if (nic.Diagnostics?.SocketError == true)
                error.WriteLine($"  {nic.Name}: {nic.Diagnostics.SocketErrorCode}: {nic.Diagnostics.ErrorMessage}");
        }
        if (report.DeadlineReached) output.WriteLine("  Scan deadline reached; results may be partial.");
        if (report.Error is not null) error.WriteLine(report.Error);
    }
}

internal sealed record CliOptions(string Command, RadioDiscoveryOptions Options);
internal sealed record DiscoveryReport(int SchemaVersion, string Command, string Status, int ExitCode,
    long DurationMilliseconds, bool DeadlineReached, InterfaceReport[] Interfaces, string? Error);
internal sealed record InterfaceReport(string Name, string Id, string Description, string? LocalAddress,
    string? SubnetMask, string InterfaceType, bool IsLoopback, bool IsApipa,
    DiscoveryDiagnostics? Diagnostics, RadioReport[] Radios);
internal sealed record RadioReport(string Protocol, string? Address, string MacAddress, string DeviceType,
    int HardwareTypeId, byte FirmwareCode, byte BetaVersion, byte Protocol2Supported, byte ReceiverCount,
    bool IsBusy, int DiscoveryPort, int PortCount);
