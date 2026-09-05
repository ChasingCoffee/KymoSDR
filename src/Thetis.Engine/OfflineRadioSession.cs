using System.Runtime.InteropServices;

namespace Thetis.Engine;

public enum SessionAudioMode { None }

public sealed record OfflineSessionOptions(int ReceiverInputRate = 192000, SessionAudioMode AudioMode = SessionAudioMode.None)
{
    internal void Validate()
    {
        if (ReceiverInputRate is not (48000 or 96000 or 192000 or 384000 or 768000 or 1536000))
            throw new ArgumentOutOfRangeException(nameof(ReceiverInputRate), "Unsupported receiver sample rate.");
        if (AudioMode != SessionAudioMode.None)
            throw new NotSupportedException("This session supports no-device audio only.");
    }
}

public sealed record OfflineSessionState(int Streams, int Receivers, int SubreceiversPerReceiver,
    int Transmitters, int SpecialStreams, int ReceiverInputRate, int AudioRate, int TransmitterOutputRate,
    int ChannelMasterWorkers, int ScopeCreates, int WavePlayCreates, int WaveRecordCreates);

/// <summary>
/// Owns the full ChannelMaster topology without sockets, hardware, audio devices or TX.
/// Use a using scope. The native library and WDSP cache remain process-owned.
/// Only one session (or DSP diagnostic) may own the shared WDSP channels at a time.
/// </summary>
public sealed class OfflineRadioSession : IDisposable
{
    private static bool active;
    private readonly SessionHandle handle = new();
    private OfflineRadioSession() { }

    public static OfflineRadioSession Open(string nativeDirectory, OfflineSessionOptions? options = null,
        CancellationToken cancellationToken = default) => OpenCore(nativeDirectory, options, cancellationToken, null);

    internal static OfflineRadioSession OpenCore(string nativeDirectory, OfflineSessionOptions? options,
        CancellationToken token, Func<int, int>? checkpoint)
    {
        options ??= new();
        options.Validate();
        token.ThrowIfCancellationRequested();
        DspRuntime.Initialize(nativeDirectory);
        lock (DspRuntime.Gate)
        {
            RequireIdle();
            token.ThrowIfCancellationRequested();
            // Verify the additional ABI before any ChannelMaster allocation.
            ReadNativeState();
            var session = new OfflineRadioSession();
            Exception? callbackError = null;
            ChannelMasterNative.Checkpoint callback = (stage, _) =>
            {
                try { return token.IsCancellationRequested ? 1 : checkpoint?.Invoke(stage) ?? 0; }
                catch (Exception ex) { callbackError = ex; return -1; } // never unwind through C
            };
            NativeMethods.ThetisWdspSetPlanningTimeLimit(0); // bounded offline startup, not radio wisdom
            int rc;
            try { rc = ChannelMasterNative.ThetisCmOpen(1, options.ReceiverInputRate, (int)options.AudioMode, 0, callback, 0); }
            finally
            {
                GC.KeepAlive(callback); // native checkpoint is synchronous, never retained
                NativeMethods.ThetisWdspSetPlanningTimeLimit(-1);
            }
            if (rc != 0)
            {
                session.handle.Dispose();
                if (rc == -4) throw new OperationCanceledException(token);
                throw new InvalidOperationException($"ChannelMaster startup failed ({rc}); completed stages were rolled back.", callbackError);
            }
            session.handle.MarkOpen();
            active = true;
            if (token.IsCancellationRequested)
            {
                session.Dispose();
                token.ThrowIfCancellationRequested();
            }
            return session;
        }
    }

    public OfflineSessionState State
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, this);
                var values = ReadNativeState();
                GC.KeepAlive(handle);
                if (values[1] != 1) throw new InvalidOperationException("Native session is not open.");
                return new(values[2], values[3], values[4], values[5], values[6], values[7], values[8],
                    values[9], values[10], values[11], values[12], values[13]);
            }
        }
    }

    internal static int[] ReadNativeState()
    {
        int[] values = new int[16];
        if (ChannelMasterNative.ThetisCmGetState(values, values.Length) != 16 || values[0] != 1 || values[14] != 0 || values[15] != 0)
            throw new NotSupportedException("Native ChannelMaster does not match the offline, no-device, no-TX ABI.");
        return values;
    }

    internal static void RequireIdle()
    {
        if (active) throw new InvalidOperationException("An offline radio session already owns the WDSP channels. Dispose it first.");
    }

    public void Dispose() => handle.Dispose();

    private sealed class SessionHandle() : SafeHandle(0, true)
    {
        public override bool IsInvalid => handle == 0;
        internal void MarkOpen() => SetHandle(1);
        protected override bool ReleaseHandle()
        {
            lock (DspRuntime.Gate)
            {
                int rc = ChannelMasterNative.ThetisCmClose();
                if (rc == 0) active = false;
                return rc == 0;
            }
        }
    }
}
