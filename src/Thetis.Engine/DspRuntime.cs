using System.Reflection;
using System.Runtime.InteropServices;

namespace Thetis.Engine;

public sealed record DspAbiInfo(string LibraryPath, int WdspVersion, int PointerBytes,
    int LongBytes, int AnalyzerInputBytes, int AnalyzerOutputBytes, int NoiseFrameSize);

/// <summary>Explicit, process-lifetime WDSP loading. Discovery never calls this.</summary>
public static class DspRuntime
{
    internal static readonly object Gate = new();
    private static nint library;
    private static DspAbiInfo? info;

    static DspRuntime() => NativeLibrary.SetDllImportResolver(typeof(DspRuntime).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != NativeMethods.Library) return 0;
        if (library == 0) throw new InvalidOperationException("Initialize DspRuntime with an explicit native directory first.");
        return library;
    }

    public static DspAbiInfo Initialize(string nativeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeDirectory);
        if (!Path.IsPathFullyQualified(nativeDirectory))
            throw new ArgumentException("Native directory must be an absolute path.", nameof(nativeDirectory));
        string filename = OperatingSystem.IsWindows() ? "thetis_wdsp.dll" :
            OperatingSystem.IsMacOS() ? "libthetis_wdsp.dylib" : "libthetis_wdsp.so";
        string path = Path.Combine(Path.GetFullPath(nativeDirectory), filename);
        lock (Gate)
        {
            if (info is not null)
            {
                if (info.LibraryPath != path) throw new InvalidOperationException("WDSP is already loaded from a different directory.");
                return info;
            }
            if (library != 0) throw new InvalidOperationException("A previous native initialization failed; restart the process before retrying.");
            library = NativeLibrary.Load(path);
            // Never unload a library after P/Invoke has cached its function addresses.
            // A failed initialization is terminal for this process, not retryable.
            int[] sizes = new int[9];
            if (NativeMethods.ThetisWdspGetAbiInfo(sizes, sizes.Length) != 9 ||
                !sizes.SequenceEqual(new[] { 1, IntPtr.Size, 4, 4, 8, 16, 8, 4, 480 }) ||
                NativeMethods.GetWDSPVersion() != 200)
                throw new NotSupportedException("Native WDSP ABI does not match this engine's modified 2.00 baseline.");
            NativeMethods.init_impulse_cache(0); // initialized once; no cross-platform cache files yet
            info = new(path, 200, sizes[1], sizes[2], sizes[6], sizes[7], sizes[8]);
            return info;
        }
    }

    internal static void RequireInitialized()
    {
        if (info is null) throw new InvalidOperationException("Initialize DspRuntime before processing signals.");
    }
}
