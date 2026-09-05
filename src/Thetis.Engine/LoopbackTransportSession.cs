using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Thetis.Engine;

public enum ProbeProtocol { P1 = 0, P2 = 1 }
// These are the inherited model IDs, NOT discovery board IDs (Saturn is 10).
public enum ProbeRadioModel { G2 = 11, HermesLite = 14 }

public sealed record LoopbackTransportOptions(string RemoteAddress = "127.0.0.1", int RemotePort = 1024,
    string LocalAddress = "127.0.0.1", int LocalPort = 0,
    ProbeProtocol Protocol = ProbeProtocol.P2, ProbeRadioModel Model = ProbeRadioModel.G2,
    bool UseRelocatedPorts = false)
{
    internal void Validate()
    {
        foreach (string text in new[] { RemoteAddress, LocalAddress })
            if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
                !IPAddress.IsLoopback(address) || address.ToString() != text)
                throw new ArgumentException("The transport probe accepts canonical IPv4 loopback addresses only.");
        if (RemotePort is < 1 or > 65518) throw new ArgumentOutOfRangeException(nameof(RemotePort));
        if (LocalPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(LocalPort));
        if (!((Protocol == ProbeProtocol.P2 && Model == ProbeRadioModel.G2) ||
              (Protocol == ProbeProtocol.P1 && Model == ProbeRadioModel.HermesLite && !UseRelocatedPorts)))
            throw new ArgumentException("Supported probe configurations are G2/P2 or Hermes Lite/P1 (without port relocation).");
    }
}

public sealed record LoopbackTransportState(int LocalPort, int RemotePort, int P2PortBase,
    ProbeProtocol Protocol, ProbeRadioModel Model, int Workers, int Datagrams, int ReceivedBytes,
    int OversizedDatagrams, int SocketErrors, int TimerTicks);

/// <summary>
/// Owns RNet buffers, a loopback-only UDP socket, a receive/discard worker and a
/// cancellable diagnostic timer. No radio parser, command sender or device is
/// connected. Dispose joins workers before releasing their socket and buffers.
/// </summary>
public sealed class LoopbackTransportSession : IDisposable
{
    private static bool active;
    private readonly TransportHandle handle = new();
    private LoopbackTransportSession() { }

    public static LoopbackTransportSession Open(string nativeDirectory, LoopbackTransportOptions? options = null,
        CancellationToken cancellationToken = default) => OpenCore(nativeDirectory, options, cancellationToken, null);

    internal static LoopbackTransportSession OpenCore(string nativeDirectory, LoopbackTransportOptions? options,
        CancellationToken token, Func<int, int>? checkpoint)
    {
        options ??= new();
        options.Validate();
        token.ThrowIfCancellationRequested();
        DspRuntime.Initialize(nativeDirectory);
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle(); // includes this probe's exclusive managed lease
            token.ThrowIfCancellationRequested();
            ReadNativeState();
            var session = new LoopbackTransportSession();
            Exception? callbackError = null;
            ChannelMasterNative.Checkpoint callback = (stage, _) =>
            {
                try { return token.IsCancellationRequested ? 1 : checkpoint?.Invoke(stage) ?? 0; }
                catch (Exception ex) { callbackError = ex; return -1; }
            };
            int rc;
            try
            {
                rc = TransportNative.ThetisTransportOpen(1, options.RemoteAddress, options.RemotePort,
                    options.LocalAddress, options.LocalPort, (int)options.Protocol, (int)options.Model,
                    options.UseRelocatedPorts ? 1 : 0, callback, 0);
            }
            finally { GC.KeepAlive(callback); } // synchronous only; never retained by native code
            if (rc != 0)
            {
                session.handle.Dispose();
                if (rc == -4) throw new OperationCanceledException(token);
                throw new InvalidOperationException($"Loopback transport startup failed ({rc}); owned resources were rolled back.", callbackError);
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

    public LoopbackTransportState State
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, this);
                var values = ReadNativeState();
                GC.KeepAlive(handle);
                if (values[1] != 1 || values[7] != 1) throw new InvalidOperationException("Native loopback transport is not open.");
                return new(values[2], values[3], values[4], (ProbeProtocol)values[5], (ProbeRadioModel)values[6],
                    values[8], values[9], values[10], values[11], values[12], values[13]);
            }
        }
    }

    internal static int[] ReadNativeState()
    {
        int[] values = new int[16];
        if (TransportNative.ThetisTransportGetState(values, values.Length) != 16 || values[0] != 1 || values[14] != 1 || values[15] != 0)
            throw new NotSupportedException("Native transport does not match the loopback-only, no-send probe ABI.");
        return values;
    }

    internal static void RequireIdle()
    {
        if (active) throw new InvalidOperationException("A loopback transport probe is already active. Dispose it first.");
    }

    public void Dispose() => handle.Dispose();

    private sealed class TransportHandle() : SafeHandle(0, true)
    {
        public override bool IsInvalid => handle == 0;
        internal void MarkOpen() => SetHandle(1);
        protected override bool ReleaseHandle()
        {
            lock (DspRuntime.Gate)
            {
                int rc = TransportNative.ThetisTransportClose();
                if (rc == 0) active = false;
                return rc == 0;
            }
        }
    }
}
