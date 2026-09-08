using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Simulator;

/// <summary>Loopback-only synthetic P2 peer. Owns one socket thread; no native DSP or hardware dependencies.</summary>
public sealed class G2Simulator : IAsyncDisposable
{
    private readonly Dictionary<int, Socket> sockets;
    private readonly CancellationTokenSource stop;
    private readonly P2Device device;
    private readonly object disposeGate = new();
    private readonly Task worker;
    private readonly SimulatorTimerResolution? timerResolution;
    private Task? disposeTask;
    private SimulatorState snapshot;
    private readonly Func<double>? testClock;
    private double lastTestTick = -1;
    internal double LastTestTick => Volatile.Read(ref lastTestTick);
    public int BasePort { get; }
    public Task Completion => worker;
    public SimulatorState State
    {
        get
        {
            var value = Volatile.Read(ref snapshot);
            return value with { Receivers = (ReceiverState[])value.Receivers.Clone() };
        }
    }

    private G2Simulator(SimulatorOptions options, Dictionary<int, Socket> bound, CancellationToken token,Func<double>? testClock)
    {
        this.testClock = testClock;
        sockets = bound; BasePort = options.BasePort;
        device = new(options); snapshot = device.Snapshot();
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            timerResolution = SimulatorTimerResolution.Acquire();
            worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch
        {
            try { timerResolution?.Dispose(); }
            finally { stop.Dispose(); }
            throw;
        }
    }

    public static G2Simulator Open(SimulatorOptions? options = null, CancellationToken cancellationToken = default)
        => OpenCore(options,cancellationToken,null);
    // Controlled-timeline integration fixture only. The public/CLI simulator
    // always uses its monotonic host clock and existing bounded replay policy.
    internal static G2Simulator OpenWithClock(SimulatorOptions options,Func<double> clock)
        => OpenCore(options,CancellationToken.None,clock ?? throw new ArgumentNullException(nameof(clock)));
    private static G2Simulator OpenCore(SimulatorOptions? options,CancellationToken cancellationToken,Func<double>? testClock)
    {
        options ??= new(); options.Validate(); cancellationToken.ThrowIfCancellationRequested();
        for (int attempt = 0; ; ++attempt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int port = options.BasePort == 0 ? Random.Shared.Next(20000, 60000) : options.BasePort;
            var bound = new Dictionary<int, Socket>();
            try
            {
                foreach (int offset in P2Device.SocketOffsets)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    bound.Add(offset, socket);
                    socket.ExclusiveAddressUse = true;
                    socket.ReceiveBufferSize = 262144; socket.SendBufferSize = 262144;
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port + offset));
                    socket.Blocking = false;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new(options with { BasePort = port }, bound, cancellationToken,testClock);
            }
            catch (SocketException ex) when (CanRetryLayout(options.BasePort, attempt, ex.SocketErrorCode, OperatingSystem.IsWindows()))
            { foreach (var socket in bound.Values) socket.Dispose(); }
            catch { foreach (var socket in bound.Values) socket.Dispose(); throw; }
        }
    }

    // Winsock can report WSAEACCES for an unavailable/exclusively reserved port,
    // not just WSAEADDRINUSE. Only automatic layout selection may try another;
    // never change OS reservations, relax exclusive binding or relocate a fixed port.
    internal static bool CanRetryLayout(int requestedPort, int attempt, SocketError error, bool isWindows) =>
        requestedPort == 0 && attempt < 99 && (error == SocketError.AddressAlreadyInUse ||
            (isWindows && error == SocketError.AccessDenied));

    private void Run()
    {
        byte[] packet = new byte[65536];
        var clock = Stopwatch.StartNew();
        double Now() => testClock?.Invoke() ?? clock.Elapsed.TotalSeconds;
        double nextSnapshot = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                // Only the six command/audio ports receive. DDC sockets exist
                // solely to give each outgoing stream its correct source port.
                var ready = Enumerable.Range(0, 6).Select(i => sockets[i]).ToList();
                Socket.Select(ready, null, null, device.Running ? 1000 : 20000);
                foreach (var socket in ready)
                {
                    int offset = ((IPEndPoint)socket.LocalEndPoint!).Port - BasePort;
                    for (int n = 0; n < 32 && !stop.IsCancellationRequested; ++n)
                    {
                        EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                        int length;
                        try { length = socket.ReceiveFrom(packet, ref source); }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                        catch (SocketException ex) when (IsPeerError(ex)) { device.SocketFailure(); break; }
                        device.Accept(offset, packet.AsSpan(0, length), (IPEndPoint)source, Now(), Send);
                    }
                    Volatile.Write(ref snapshot, device.Snapshot());
                }
                double now = Now(); device.Tick(now, Send);
                if (testClock is not null || now >= nextSnapshot)
                { Volatile.Write(ref snapshot, device.Snapshot()); nextSnapshot = now + 0.05; }
                if (testClock is not null) Volatile.Write(ref lastTestTick,now);
            }
        }
        finally
        {
            try
            {
                device.Shutdown(); Volatile.Write(ref snapshot, device.Snapshot());
                foreach (var socket in sockets.Values) socket.Dispose();
            }
            finally { timerResolution?.Dispose(); }
        }
    }

    private void Send(int offset, byte[] packet, IPEndPoint target)
    {
        // Defense in depth: no callback or control packet can select a LAN peer.
        if (!IPAddress.IsLoopback(target.Address) || target.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Non-loopback simulator destination rejected.");
        try { sockets[offset].SendTo(packet, target); }
        catch (SocketException ex) when (IsPeerError(ex) || ex.SocketErrorCode == SocketError.WouldBlock)
        { device.SocketFailure(); }
    }
    private static bool IsPeerError(SocketException ex) => ex.SocketErrorCode is SocketError.ConnectionReset or
        SocketError.ConnectionRefused or SocketError.HostUnreachable or SocketError.NetworkUnreachable;

    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        stop.Cancel();
        try { await worker.ConfigureAwait(false); }
        finally { stop.Dispose(); }
    }
}
