using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

namespace Thetis.Headless;

internal static class ReceiveSoakCli
{
    internal const string Help = """
        Usage: Thetis.Headless receive-soak --native-dir ABSOLUTE_PATH [--duration-seconds 10..7200] [--reconnects 1..20]
        Own loopback simulator -> native P2/CM/WDSP audio and spectrum. No hardware, TX or audio devices.
        Default: 60 seconds of sustained RX, followed by loss, slow-reader, peer-loss and 2 reconnect checks.
        For 30 minutes: --duration-seconds 1800 --reconnects 10. Fault/startup/cleanup time is additional.
        Final JSON (including partial failures/cancellation) goes to stdout; progress goes to stderr.
        Exit: 0 pass/help, 2 syntax, 3 incompatible/missing native library, 4 failed checks, 130 cancelled.
        """;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken token = default,
        Func<string, ReceiveSoakOptions, Action<ReceiveSoakProgress>, CancellationToken, Task<ReceiveSoakResult>>? runner = null)
    {
        if (args is [_, "--help"]) { output.WriteLine(Help); return 0; }
        string directory; ReceiveSoakOptions options;
        try { (directory, options) = Parse(args); }
        catch (ArgumentException) { error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? ReceiveSoak.RunAsync)(directory, options,
                p => error.WriteLine($"[{p.Phase}] {p.ElapsedSeconds:F0}s: IQ={p.IqPackets}, audio={p.AudioFrames}, spectra={p.SpectrumFrames}, RSS={p.WorkingSetBytes / 1048576.0:F1} MiB"), token);
            output.WriteLine(JsonSerializer.Serialize(result, Json));
            return result.Cancelled ? 130 : result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("Receive soak cancelled before measurement; owners disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load compatible native receive code: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or SocketException)
        { error.WriteLine($"Receive soak failed: {ex.Message}"); return 4; }
    }

    internal static (string Directory, ReceiveSoakOptions Options) Parse(string[] args)
    {
        string? directory = null;
        int seconds = 60, reconnects = 2;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args.Length == 0 || args[0] != "receive-soak") throw new ArgumentException();
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i])) throw new ArgumentException();
            switch (args[i])
            {
                case "--native-dir": directory = args[i + 1]; break;
                case "--duration-seconds": seconds = Number(args[i + 1]); break;
                case "--reconnects": reconnects = Number(args[i + 1]); break;
                default: throw new ArgumentException(); // no peer address, NIC or TX escape hatch
            }
        }
        if (directory is null || !Path.IsPathFullyQualified(directory)) throw new ArgumentException();
        var options = new ReceiveSoakOptions(seconds, reconnects); options.Validate();
        return (directory, options);
    }
    private static int Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
        ? n : throw new ArgumentException();
}
