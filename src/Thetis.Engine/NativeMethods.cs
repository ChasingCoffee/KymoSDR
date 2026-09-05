using System.Runtime.InteropServices;

namespace Thetis.Engine;

// Match the project's native source, not stock TAPR signatures. In particular,
// GetPixels has a fifth double* result. No C long or native bool crosses this ABI.
internal static unsafe class NativeMethods
{
    internal const string Library = "thetis_wdsp";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern int GetWDSPVersion();
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern int ThetisWdspGetAbiInfo([Out] int[] values, int capacity);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void ThetisWdspSetPlanningTimeLimit(double seconds);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void init_impulse_cache(int use);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern nint create_resample(int run, int size, double* input, double* output,
        int inputRate, int outputRate, double cutoff, int coefficients, double gain);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern int xresample(nint resampler);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void destroy_resample(nint resampler);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void flush_resample(nint resampler);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void XCreateAnalyzer(int display, out int success, int maxSize, int maxLo,
        int maxStitch, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void DestroyAnalyzer(int display);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetAnalyzer(int display, int pixelOutputs, int ffts, int type,
        [In] int[] flips, int size, int bufferSize, int window, double piAlpha, int overlap,
        int clip, double lowClip, double highClip, int pixels, int stitch, int calibration,
        double minFrequency, double maxFrequency, int maxWriteAhead);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetPixelRef(int display, double reference);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void Spectrum0(int run, int display, int span, int lo, [In] double[] iq);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void GetPixels(int display, int output, [Out] float[] pixels, out int ready, out double reference);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void OpenChannel(int channel, int inputSize, int dspSize, int inputRate,
        int dspRate, int outputRate, int type, int state, double delayUp, double slewUp,
        double delayDown, double slewDown, int blockForOutput);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void CloseChannel(int channel);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetRXAMode(int channel, int mode);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetRXABandpassFreqs(int channel, double low, double high);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetRXAAGCMode(int channel, int mode);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void SetRXAAGCFixed(int channel, double decibels);
    [DllImport(Library, CallingConvention = Cdecl, ExactSpelling = true)]
    internal static extern void fexchange0(int channel, [In] double[] input, [Out] double[] output, out int error);
}
