using Avalonia;

namespace Thetis.Desktop;

public sealed record PreviewLaunchOptions(string NativeDirectory, bool Smoke = false, string? Screenshot = null)
{
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR") ??
        (Directory.Exists(Path.Combine(AppContext.BaseDirectory,"native")) ? Path.Combine(AppContext.BaseDirectory,"native") :
            Path.GetFullPath(Path.Combine("artifacts","native","stage","Release")));
    public static PreviewLaunchOptions Parse(string[] args)
    {
        string directory = DefaultDirectory; bool smoke = false; string? screenshot = null;
        var seen = new HashSet<string>();
        for (int i = 0; i < args.Length; ++i)
        {
            string key = args[i];
            if (!seen.Add(key)) throw new ArgumentException($"Duplicate option: {key}");
            if (key == "--smoke") smoke = true;
            else if (key is "--native-dir" or "--screenshot")
            {
                if (++i == args.Length || !Path.IsPathFullyQualified(args[i])) throw new ArgumentException($"{key} requires an absolute path.");
                if (key == "--native-dir") directory = args[i]; else screenshot = args[i];
            }
            else throw new ArgumentException($"Unknown option: {key}. Hardware addresses and TX are unavailable.");
        }
        return new(directory,smoke,screenshot);
    }
}
public static class Program
{
    internal static PreviewLaunchOptions Launch { get; private set; } = new(PreviewLaunchOptions.DefaultDirectory);
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--help"])
        { Console.WriteLine("KymoSDR simulator receiver: [--native-dir ABSOLUTE_PATH] [--smoke] [--screenshot ABSOLUTE_PNG]\nNo radio/TX options. Starts disconnected; playback defaults to no-device output, muted at -40 dB."); return 0; }
        try { Launch = PreviewLaunchOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Desktop startup failed: {ex.Message}\nA working graphical desktop session is required. No hardware connection is attempted by this preview.");
            return 4;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
