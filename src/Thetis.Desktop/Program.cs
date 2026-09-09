using Avalonia;
using Thetis.Preview;

namespace Thetis.Desktop;

public sealed record PreviewLaunchOptions(string NativeDirectory, bool Smoke = false, string? Screenshot = null,
    bool PersistSettings = true,int EnduranceSeconds = 0,string? ReportPath = null,G2RadioTarget? G2Target = null)
{
    public bool Automated => Smoke || EnduranceSeconds > 0;
    public static string DefaultDirectory => Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR") ??
        (Directory.Exists(Path.Combine(AppContext.BaseDirectory,"native")) ? Path.Combine(AppContext.BaseDirectory,"native") :
            Path.GetFullPath(Path.Combine("artifacts","native","stage","Release")));
    public static PreviewLaunchOptions Parse(string[] args)
    {
        string directory = DefaultDirectory; bool smoke = false,persist = true; string? screenshot = null,report = null; int endurance = 0;
        string? nic = null,radio = null,mac = null;
        var seen = new HashSet<string>();
        for (int i = 0; i < args.Length; ++i)
        {
            string key = args[i];
            if (!seen.Add(key)) throw new ArgumentException($"Duplicate option: {key}");
            if (key == "--smoke") smoke = true;
            else if (key == "--no-settings") persist = false;
            else if (key is "--g2-nic" or "--g2-target" or "--g2-mac")
            {
                if (++i == args.Length || args[i].StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException($"Missing {key} value.");
                if (key == "--g2-nic") nic = args[i]; else if (key == "--g2-target") radio = args[i]; else mac = args[i];
            }
            else if (key == "--endurance-seconds")
            {
                if (++i == args.Length || !int.TryParse(args[i],out endurance) || endurance is < 30 or > 3600)
                    throw new ArgumentException("--endurance-seconds requires 30–3600 wall-clock seconds.");
            }
            else if (key is "--native-dir" or "--screenshot" or "--report")
            {
                if (++i == args.Length || !Path.IsPathFullyQualified(args[i])) throw new ArgumentException($"{key} requires an absolute path.");
                if (key == "--native-dir") directory = args[i]; else if (key == "--report") report = args[i]; else screenshot = args[i];
            }
            else throw new ArgumentException($"Unknown option: {key}. Automatic connection, unmute and TX are unavailable.");
        }
        if (smoke && endurance > 0) throw new ArgumentException("Choose smoke or endurance, not both.");
        if (endurance > 0 && report is null) throw new ArgumentException("Endurance requires --report ABSOLUTE_JSON.");
        if (report is not null && endurance == 0) throw new ArgumentException("--report is for endurance; use Export diagnostics in an interactive window.");
        G2RadioTarget? target = null;
        if (nic is not null || radio is not null || mac is not null)
        {
            if (nic is null || radio is null || mac is null || smoke || endurance > 0)
                throw new ArgumentException("Supply all three --g2-nic/--g2-target/--g2-mac prefill values, never with automation.");
            target = new(nic,radio,mac); target.Validate();
        }
        return new(directory,smoke,screenshot,persist && !smoke && endurance == 0,endurance,report,target);
    }
}
public static class Program
{
    internal static PreviewLaunchOptions Launch { get; private set; } = new(PreviewLaunchOptions.DefaultDirectory);
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--help"])
        { Console.WriteLine($"KymoSDR receive preview: [--native-dir ABSOLUTE_PATH] [--no-settings] [--smoke] [--screenshot ABSOLUTE_PNG]\nG2 form prefill only: --g2-nic LOCAL_IPV4 --g2-target RADIO_IPV4 --g2-mac MAC (starts disconnected; ANT1 confirmation required)\nEndurance: --endurance-seconds 30..3600 --report ABSOLUTE_JSON (simulator only, no settings or physical audio)\nInteractive discovery is Ethernet-only and explicit. Saved radio/audio selections may be restored; no automatic connection/unmute or TX. Startup is muted at -40 dB; missing/ambiguous output uses no device.\nPreferences: {PreviewPreferencesStore.DefaultPath}"); return 0; }
        try { Launch = PreviewLaunchOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Desktop failed: {ex.Message}\nA working graphical desktop session is required. G2 connections require explicit confirmation and have an independent native receive deadline.");
            return 4;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
