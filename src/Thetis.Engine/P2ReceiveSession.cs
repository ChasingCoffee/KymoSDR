using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Thetis.Engine;

public sealed record P2ReceiveOptions(int BasePort, int Ddc = 2, int InputRate = 192000,
    int FrequencyHz = 14_199_000, string Address = "127.0.0.1")
{
    internal void Validate()
    {
        if (BasePort is < 1024 or > 65515) throw new ArgumentOutOfRangeException(nameof(BasePort));
        if (Ddc is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(Ddc));
        if (InputRate is not (48000 or 96000 or 192000 or 384000)) throw new ArgumentOutOfRangeException(nameof(InputRate));
        ValidateFrequency(FrequencyHz);
        if (!IPAddress.TryParse(Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(ip) || ip.ToString() != Address)
            throw new ArgumentException("P2 integration currently accepts canonical IPv4 loopback addresses only.");
    }
    internal static void ValidateFrequency(int frequency)
    { if (frequency is < 0 or > 61_440_000) throw new ArgumentOutOfRangeException(nameof(frequency)); }
}

public sealed record P2ReceiveState(int LocalPort, int BasePort, int Ddc, int InputRate, int SocketWorkers,
    long IqPackets, long IqSamples, long MissingPackets, long LatePackets, long MalformedPackets,
    long ForeignPackets, long MicPacketsDiscarded, long StatusPackets, long SocketErrors, long CommandsSent,
    long InputOverruns, long AudioQueued, long AudioDropped, long AudioProduced, long DspErrors);

/// <summary>Loopback P2 -> native router/CM buffer -> WDSP spectrum and USB audio. No hardware or TX operation.</summary>
public sealed class P2ReceiveSession : IDisposable
{
    private static bool active;
    private readonly ReceiveHandle handle = new();
    private readonly float[] spectrumPixels = new float[ReceiveSpectrumFrame.NativePixelCount];
    private readonly long[] spectrumMetadata = new long[12];
    private P2ReceiveSession() { }

    public static P2ReceiveSession Open(string nativeDirectory, P2ReceiveOptions options,
        CancellationToken cancellationToken = default) => OpenCore(nativeDirectory, options, cancellationToken, null);

    internal static P2ReceiveSession OpenCore(string nativeDirectory, P2ReceiveOptions options,
        CancellationToken token, Func<int, int>? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(); token.ThrowIfCancellationRequested();
        DspRuntime.Initialize(nativeDirectory);
        return NativeLifecycle.Invoke(() => OpenOnLifecycle(options, token, checkpoint));
    }

    private static P2ReceiveSession OpenOnLifecycle(P2ReceiveOptions options, CancellationToken token,
        Func<int, int>? checkpoint)
    {
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle(); token.ThrowIfCancellationRequested(); ReadNativeState();
            if (P2ReceiveNative.ThetisP2ReceiveSpectrumAbi() != 1)
                throw new NotSupportedException("Native receive spectrum ABI is incompatible.");
            var session = new P2ReceiveSession();
            Exception? callbackError = null;
            ChannelMasterNative.Checkpoint callback = (stage, _) =>
            {
                try { return token.IsCancellationRequested ? 1 : checkpoint?.Invoke(stage) ?? 0; }
                catch (Exception ex) { callbackError = ex; return -1; }
            };
            NativeMethods.ThetisWdspSetPlanningTimeLimit(0);
            int rc;
            try { rc = P2ReceiveNative.ThetisP2ReceiveOpen(1, options.Address, options.BasePort, options.Ddc,
                options.InputRate, options.FrequencyHz, callback, 0); }
            finally { GC.KeepAlive(callback); NativeMethods.ThetisWdspSetPlanningTimeLimit(-1); }
            if (rc != 0)
            {
                session.handle.Dispose();
                if (rc == -4) throw new OperationCanceledException(token);
                throw new InvalidOperationException($"Native P2 receive startup failed ({rc}); completed stages were rolled back.", callbackError);
            }
            session.handle.MarkOpen(); active = true;
            if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
            return session;
        }
    }

    public P2ReceiveState State
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                CheckOpen(); var s = ReadNativeState(); GC.KeepAlive(handle);
                if (s[1] != 1) throw new InvalidOperationException("Native P2 session is not open.");
                return new((int)s[2], (int)s[3], (int)s[4], (int)s[5], (int)s[6], s[7], s[8], s[9], s[10],
                    s[11], s[12], s[13], s[14], s[15], s[16], s[17], s[18], s[19], s[20], s[21]);
            }
        }
    }
    public void Tune(int frequencyHz)
    {
        P2ReceiveOptions.ValidateFrequency(frequencyHz);
        lock (DspRuntime.Gate)
        {
            CheckOpen(); int rc = P2ReceiveNative.ThetisP2ReceiveTune(frequencyHz); GC.KeepAlive(handle);
            if (rc != 0) throw new InvalidOperationException($"Native P2 tuning failed ({rc}).");
        }
    }
    /// <summary>Returns frames copied into an interleaved L/R buffer (48 kHz), without waiting.</summary>
    public int ReadAudio(double[] interleaved)
    {
        ArgumentNullException.ThrowIfNull(interleaved);
        if (interleaved.Length is < 2 or > 32768 || interleaved.Length % 2 != 0)
            throw new ArgumentException("Audio buffer must hold 1..16384 interleaved stereo frames.");
        lock (DspRuntime.Gate)
        {
            CheckOpen(); int frames = P2ReceiveNative.ThetisP2ReceiveReadAudio(interleaved, interleaved.Length / 2);
            GC.KeepAlive(handle);
            if (frames < 0) throw new InvalidOperationException($"Native audio pull failed ({frames}).");
            return frames;
        }
    }
    private void CheckOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, this);
    /// <summary>Returns the latest unread spectrum, or null without waiting for new input.
    /// Slow readers coalesce frames; neither native nor managed code queues a history.</summary>
    public ReceiveSpectrumFrame? ReadSpectrum()
    {
        lock (DspRuntime.Gate)
        {
            CheckOpen();
            int count = P2ReceiveNative.ThetisP2ReceiveReadSpectrum(1, spectrumPixels, spectrumPixels.Length,
                spectrumMetadata, spectrumMetadata.Length);
            GC.KeepAlive(handle);
            if (count == 0) return null;
            if (count != spectrumPixels.Length) throw new InvalidOperationException($"Native spectrum pull failed ({count}).");
            return new((float[])spectrumPixels.Clone(), spectrumMetadata);
        }
    }
    internal static long[] ReadNativeState()
    {
        long[] state = new long[24];
        if (P2ReceiveNative.ThetisP2ReceiveGetState(state, state.Length) != 24 || state[0] != 1 || state[22] != 1 || state[23] != 0)
            throw new NotSupportedException("Native P2 ABI does not match the loopback-only, RX-only contract.");
        return state;
    }
    internal static void RequireIdle()
    { if (active) throw new InvalidOperationException("A P2 receive session already owns the native channels and transport."); }
    public void Dispose() => handle.Dispose();
    private sealed class ReceiveHandle() : SafeHandle(0, true)
    {
        public override bool IsInvalid => handle == 0;
        internal void MarkOpen() => SetHandle(1);
        protected override bool ReleaseHandle() => NativeLifecycle.Invoke(CloseOnLifecycle);
        private static bool CloseOnLifecycle()
        {
            lock (DspRuntime.Gate)
            {
                int rc = P2ReceiveNative.ThetisP2ReceiveClose();
                if (rc == 0) active = false;
                return rc == 0;
            }
        }
    }
}

internal static class P2ReceiveNative
{
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveOpen(int abi, [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
        int basePort, int ddc, int rate, int frequency, ChannelMasterNative.Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveClose();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveTune(int frequency);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveGetState([Out] long[] values, int capacity);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveReadAudio([Out] double[] samples, int capacityFrames);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveSpectrumAbi();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveReadSpectrum(int abi, [Out] float[] pixels, int capacity,
        [Out] long[] metadata, int metadataCapacity);
}
