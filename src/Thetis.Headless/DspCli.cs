using System.Text.Json;
using Thetis.Engine;

namespace Thetis.Headless;

internal static class DspCli
{
    private const string Help = "Usage: Thetis.Headless dsp-selftest --native-dir ABSOLUTE_PATH\nOffline only; no radio, TX, audio devices or network. Outputs JSON.";
    internal static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken token = default,
        Func<string, CancellationToken, DspSelfTestResult>? runner = null)
    {
        if (args.Length == 2 && args[1] == "--help") { output.WriteLine(Help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(Help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = (runner ?? DspDiagnostics.Run)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
            { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 1;
        }
        catch (OperationCanceledException) { error.WriteLine("DSP self-test cancelled."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load a compatible native DSP build: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException)
        { error.WriteLine($"DSP self-test failed: {ex.Message}"); return 4; }
    }
}
