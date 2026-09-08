using System.Runtime.InteropServices;

namespace Thetis.Engine;

public sealed record ReceiveState(int LocalPort, int BasePort, int Ddc, int InputRate, int SocketWorkers,
    long IqPackets, long IqSamples, long MissingPackets, long LatePackets, long MalformedPackets,
    long ForeignPackets, long MicPacketsDiscarded, long StatusPackets, long SocketErrors, long CommandsSent,
    long InputOverruns, long AudioQueued, long AudioDropped, long AudioProduced, long DspErrors);

/// <summary>Shared loopback receive lifecycle, controls, diagnostics and bounded audio/spectrum pulls.</summary>
public abstract class ReceiveSession : IDisposable
{
    private static bool active;
    private readonly ReceiveHandle handle = new();
    private readonly float[] spectrumPixels = new float[ReceiveSpectrumFrame.NativePixelCount];
    private readonly long[] spectrumMetadata = new long[12];
    private protected ReceiveSession() { }

    private protected static T OpenSession<T>(string nativeDirectory, P2ReceiveOptions options, int protocol,
        CancellationToken token, Func<int, int>? checkpoint, Func<T> create) where T : ReceiveSession
    {
        ArgumentNullException.ThrowIfNull(options); options.Validate(protocol == 1); token.ThrowIfCancellationRequested();
        DspRuntime.Initialize(nativeDirectory);
        return NativeLifecycle.Invoke(() => OpenOnLifecycle(options, protocol, token, checkpoint, create));
    }

    private static T OpenOnLifecycle<T>(P2ReceiveOptions options, int protocol, CancellationToken token,
        Func<int, int>? checkpoint, Func<T> create) where T : ReceiveSession
    {
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle(); token.ThrowIfCancellationRequested(); ReadNativeState();
            if (P2ReceiveNative.ThetisP2ReceiveSpectrumAbi() != 1)
                throw new NotSupportedException("Native receive spectrum ABI is incompatible.");
            if (P2ReceiveNative.ThetisP2ReceiveControlsAbi() != 1)
                throw new NotSupportedException("Native receive controls ABI is incompatible.");
            if (P2ReceiveNative.ThetisReceiveProtocolAbi() != 1)
                throw new NotSupportedException("Native receive protocol ABI is incompatible.");
            var controls = options.Demodulation ?? new();
            var session = create();
            Exception? callbackError = null;
            ChannelMasterNative.Checkpoint callback = (stage, _) =>
            {
                try { return token.IsCancellationRequested ? 1 : checkpoint?.Invoke(stage) ?? 0; }
                catch (Exception ex) { callbackError = ex; return -1; }
            };
            NativeMethods.ThetisWdspSetPlanningTimeLimit(0);
            int rc;
            try { rc = P2ReceiveNative.ThetisReceiveOpenWithControls(1, protocol, options.Address, options.BasePort, options.Ddc,
                options.InputRate, options.FrequencyHz, (int)controls.Mode, controls.LowCutHz, controls.HighCutHz, callback, 0); }
            finally { GC.KeepAlive(callback); NativeMethods.ThetisWdspSetPlanningTimeLimit(-1); }
            if (rc != 0)
            {
                session.handle.Dispose();
                if (rc == -4) throw new OperationCanceledException(token);
                throw new InvalidOperationException($"Native receive startup failed ({rc}); completed stages were rolled back.", callbackError);
            }
            session.handle.MarkOpen(); active = true;
            if (token.IsCancellationRequested) { session.Dispose(); token.ThrowIfCancellationRequested(); }
            return session;
        }
    }

    public ReceiveState State
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                CheckOpen(); var s = ReadNativeState(); GC.KeepAlive(handle);
                if (s[1] != 1) throw new InvalidOperationException("Native receive session is not open.");
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
            if (rc != 0) throw new InvalidOperationException($"Native receive tuning failed ({rc}).");
        }
    }
    public ReceiveDemodulationState Demodulation
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                CheckOpen(); long[] values = new long[8];
                int count = P2ReceiveNative.ThetisP2ReceiveGetControls(values, values.Length); GC.KeepAlive(handle);
                if (count != 8 || values[0] != 1 || values[1] != 1 || values[2] is not (0 or 1) || values[7] < 1)
                    throw new NotSupportedException("Native receive control state is incompatible.");
                var settings = new ReceiveDemodulation((ReceiveMode)values[2], checked((int)values[3]), checked((int)values[4]));
                settings.Validate();
                if (values[5] != settings.SignedLowHz || values[6] != settings.SignedHighHz)
                    throw new NotSupportedException("Native receive passband mapping is incompatible.");
                return new(settings, values[7]);
            }
        }
    }
    /// <summary>Applies mode and filter together, clearing queued tap audio. Allow DSP history
    /// to settle before measuring; no click-free or sample-accurate transition is promised.</summary>
    public void ConfigureDemodulation(ReceiveDemodulation settings)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        // FIR updates allocate native memory: keep them on the lifecycle thread too.
        NativeLifecycle.Invoke(() =>
        {
            lock (DspRuntime.Gate)
            {
                CheckOpen();
                int rc = P2ReceiveNative.ThetisP2ReceiveSetControls(1, (int)settings.Mode, settings.LowCutHz, settings.HighCutHz);
                GC.KeepAlive(handle);
                if (rc != 0) throw new InvalidOperationException($"Native receive control update failed ({rc}).");
                return 0;
            }
        });
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
            throw new NotSupportedException("Native receive ABI does not match the loopback-only, RX-only contract.");
        return state;
    }
    internal static void RequireIdle()
    { if (active) throw new InvalidOperationException("A receive session already owns the native channels and transport."); }
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
    internal static extern int ThetisReceiveProtocolAbi();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisReceiveOpenWithControls(int abi, int protocol, [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
        int basePort, int ddc, int rate, int frequency, int mode, int low, int high, ChannelMasterNative.Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveOpenWithControls(int abi, [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
        int basePort, int ddc, int rate, int frequency, int mode, int low, int high, ChannelMasterNative.Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveControlsAbi();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveSetControls(int abi, int mode, int low, int high);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisP2ReceiveGetControls([Out] long[] values, int capacity);
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
