using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Simulator;

public sealed record P1SimulatorOptions(int Port = 0, int DropEvery = 0, bool InjectFaults = false);
public sealed record P1SimulatorState(bool Running, int ClientPort, int FrequencyHz, long Starts, long Stops,
    long Packets, long InjectedDrops, long UnsafeRequests, long MalformedRequests, long ForeignRequests,
    long SocketErrors, long WatchdogStops, long PacingResyncs);

/// <summary>Deterministic single-RX P1 UDP fixture, always bound to IPv4 loopback.
/// Fixed RF tone 14.2 MHz, complex amplitude .25, 48 kHz. No transmit implementation.</summary>
public sealed class P1Simulator : IAsyncDisposable
{
    private readonly Socket socket;
    private readonly P1SimulatorOptions options;
    private readonly CancellationTokenSource stop;
    private readonly SimulatorTimerResolution timer;
    private readonly Task worker;
    private readonly object disposeGate = new();
    private Task? disposal;
    private P1SimulatorState snapshot = new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private IPEndPoint? owner;
    private bool configured, adcSelected, running, injected;
    private int frequency;
    private uint sequence;
    private double phase, nextPacket, lastControl;
    private long starts, stops, packets, drops, unsafeRequests, malformed, foreign, errors, watchdog, resyncs;
    public int Port { get; }
    public Task Completion => worker;
    public P1SimulatorState State => Volatile.Read(ref snapshot);

    private P1Simulator(Socket bound, P1SimulatorOptions settings, CancellationToken token)
    {
        socket = bound; options = settings; Port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            timer = SimulatorTimerResolution.Acquire();
            try { worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); }
            catch { timer.Dispose(); throw; }
        }
        catch { stop.Dispose(); throw; }
    }

    public static P1Simulator Open(P1SimulatorOptions? options = null, CancellationToken token = default)
    {
        options ??= new(); token.ThrowIfCancellationRequested();
        if (options.Port != 0 && options.Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.DropEvery is < 0 or 1) throw new ArgumentOutOfRangeException(nameof(options));
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.ExclusiveAddressUse = true; socket.ReceiveBufferSize = socket.SendBufferSize = 262144;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, options.Port)); socket.Blocking = false;
            return new(socket, options, token);
        }
        catch { socket.Dispose(); throw; }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew(); byte[] input = new byte[65536];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                socket.Poll(running ? 1000 : 20000, SelectMode.SelectRead);
                for (int i = 0; i < 32; ++i)
                {
                    EndPoint source = new IPEndPoint(IPAddress.Any, 0);
                    int length;
                    try { length = socket.ReceiveFrom(input, ref source); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                    Accept(input.AsSpan(0, length), (IPEndPoint)source, clock.Elapsed.TotalSeconds);
                }
                double now = clock.Elapsed.TotalSeconds;
                if (running && now - lastControl >= 2) { running = configured = adcSelected = false; owner = null; ++watchdog; }
                for (int burst = 0; running && now >= nextPacket && burst < 8; ++burst)
                {
                    byte[] packet = MakePacket(); ++packets;
                    if (options.DropEvery > 0 && packets % options.DropEvery == 0) ++drops;
                    else Send(packet, socket);
                    if (options.InjectFaults && !injected)
                    {
                        injected = true;
                        Send(packet, socket); // duplicate, must not reenter DSP
                        byte[] bad = (byte[])packet.Clone(); bad[520] = 0; Send(bad, socket);
                        using var stranger = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        stranger.Bind(new IPEndPoint(IPAddress.Loopback, 0)); Send(packet, stranger);
                    }
                    nextPacket += 126.0 / 48000;
                }
                if (running && now >= nextPacket) { nextPacket = now + 126.0 / 48000; ++resyncs; }
                Publish();
            }
        }
        catch (SocketException) { ++errors; }
        finally { running = false; Publish(); socket.Dispose(); timer.Dispose(); }
    }

    private void Accept(ReadOnlySpan<byte> p, IPEndPoint peer, double now)
    {
        if (!IPAddress.IsLoopback(peer.Address) || peer.AddressFamily != AddressFamily.InterNetwork ||
            owner is not null && !owner.Equals(peer)) { ++foreign; return; }
        if (p.Length >= 3 && p[0] == 0xef && p[1] == 0xfe && p[2] == 4)
        {
            if (p.Length != 64 || p[3] > 1 || p[4..].ContainsAnyExcept((byte)0)) { ++unsafeRequests; return; }
            if (p[3] == 0)
            { if (owner is not null) ++stops; running = configured = adcSelected = false; owner = null; return; }
            if (!configured) { ++malformed; return; }
            if (!running) { running = true; ++starts; sequence = uint.MaxValue - 1; phase = 0; injected = false; nextPacket = now; }
            lastControl = now; return;
        }
        if (p.Length != 1032 || p[0] != 0xef || p[1] != 0xfe || p[2] != 1 || p[3] != 2 ||
            !p.Slice(8, 3).SequenceEqual(new byte[] {127,127,127}) || !p.Slice(520, 3).SequenceEqual(new byte[] {127,127,127}))
        { ++malformed; return; }
        // Only the exact receive subset is accepted, regardless of whether MOX is set.
        if (p.Slice(11, 5).ContainsAnyExcept((byte)0) || p[523] is not (4 or 28) ||
            p.Slice(16, 504).ContainsAnyExcept((byte)0) || p.Slice(528, 504).ContainsAnyExcept((byte)0))
        { ++unsafeRequests; return; }
        if (p[523] == 28)
        {
            if (p.Slice(524,4).ContainsAnyExcept((byte)0)) { ++unsafeRequests; return; }
            adcSelected = true; owner = peer; lastControl = now; return;
        }
        uint hz = BinaryPrimitives.ReadUInt32BigEndian(p[524..]);
        if (hz > 61_440_000) { ++malformed; return; }
        frequency = (int)hz; configured = adcSelected; owner = peer; lastControl = now;
    }

    private byte[] MakePacket()
    {
        byte[] p = new byte[1032]; p[0] = 0xef; p[1] = 0xfe; p[2] = 1; p[3] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), sequence++);
        double step = 2 * Math.PI * (14_200_000 - frequency) / 48000;
        for (int f = 0; f < 2; ++f)
        {
            int frame = 8 + 512 * f; p[frame] = p[frame+1] = p[frame+2] = 127;
            p[frame+3] = 7; // incoming PTT/dot/dash are deliberately ignored by the receiver
            for (int i = 0; i < 63; ++i)
            {
                int at = frame + 8 + 8*i;
                Put24(p.AsSpan(at), (int)(.25 * 8388608 * Math.Cos(phase)));
                Put24(p.AsSpan(at+3), (int)(.25 * 8388608 * Math.Sin(phase)));
                p[at+6] = 0x7f; p[at+7] = 0xff; // loud microphone sentinel, never demodulated
                phase = Math.IEEERemainder(phase + step, 2 * Math.PI);
            }
        }
        return p;
    }
    private static void Put24(Span<byte> p, int value)
    { p[0] = (byte)(value >> 16); p[1] = (byte)(value >> 8); p[2] = (byte)value; }
    private void Send(byte[] packet, Socket sender)
    {
        if (owner is null || !IPAddress.IsLoopback(owner.Address)) throw new InvalidOperationException("P1 loopback owner required.");
        try { sender.SendTo(packet, owner); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.ConnectionRefused) { ++errors; }
    }
    private void Publish() => Volatile.Write(ref snapshot, new(running, owner?.Port ?? 0, frequency, starts, stops,
        packets, drops, unsafeRequests, malformed, foreign, errors, watchdog, resyncs));
    public ValueTask DisposeAsync() { lock (disposeGate) return new(disposal ??= DisposeCore()); }
    private async Task DisposeCore()
    { stop.Cancel(); try { await worker.ConfigureAwait(false); } finally { stop.Dispose(); } }
}
