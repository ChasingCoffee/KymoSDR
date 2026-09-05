using System.Text.Json;
using Thetis.Engine;

namespace Thetis.Headless;

internal static class SessionCli
{
    private const string Help = "Usage: Thetis.Headless session-selftest --native-dir ABSOLUTE_PATH\n100 offline ChannelMaster lifecycles. No network, radio, TX or audio devices. Outputs JSON.";
    internal static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken token = default,
        Func<string, CancellationToken, SessionSelfTestResult>? runner = null)
    {
        if (args.Length == 2 && args[1] == "--help") { output.WriteLine(Help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = (runner ?? SessionDiagnostics.Run)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 1;
        }
        catch (OperationCanceledException) { error.WriteLine("Session self-test cancelled; completed startup stages were disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load a compatible native session build: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException)
        { error.WriteLine($"Session self-test failed: {ex.Message}"); return 4; }
    }
}
