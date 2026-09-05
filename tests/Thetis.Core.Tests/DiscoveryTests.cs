using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Thetis.Core.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Radio = IPAddress.Parse("192.0.2.20");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");

    private static RadioDiscoveryOptions Options() => new()
    {
        ScanPerformance = ScanPerformanceProfile.UltraFast,
        MaxScanMilliseconds = 1000,
        IncludeGeneralBroadcast = false
    };

    internal static byte[] Fixture(string name) => Convert.FromHexString(
        string.Concat(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));

    [TestMethod]
    public void ParsesP1HermesLiteFixture()
    {
        var data = Fixture("p1-hermes-lite.hex");
        var parsed = new RadioDiscoveryService().parseDiscoveryReply(data, data.Length, Radio, Options());
        Assert.IsTrue(parsed.IsDiscovery);
        Assert.AreEqual(RadioDiscoveryRadioProtocol.P1, parsed.Protocol);
        Assert.AreEqual(HPSDRHW.HermesLite, parsed.DeviceType);
        Assert.AreEqual("02-00-00-00-00-01", parsed.MacAddress);
        Assert.AreEqual((byte)73, parsed.CodeVersion);
        Assert.AreEqual((byte)2, parsed.NumRxs);
        Assert.IsFalse(parsed.IsBusy);
    }

    [TestMethod]
    public void ParsesP2SaturnFixtureIncludingBusyAndVersionFields()
    {
        var data = Fixture("p2-saturn.hex");
        data[4] = 3;
        var parsed = new RadioDiscoveryService().parseDiscoveryReply(data, data.Length, Radio, Options());
        Assert.IsTrue(parsed.IsDiscovery);
        Assert.IsTrue(parsed.IsBusy);
        Assert.AreEqual(RadioDiscoveryRadioProtocol.P2, parsed.Protocol);
        Assert.AreEqual(HPSDRHW.Saturn, parsed.DeviceType);
        Assert.AreEqual((byte)42, parsed.CodeVersion);
        Assert.AreEqual((byte)3, parsed.BetaVersion);
        Assert.AreEqual((byte)2, parsed.ProtocolSupported);
        Assert.AreEqual((byte)10, parsed.NumRxs);
    }

    [TestMethod]
    public void ParserRejectsTruncatedInvalidAndOutOfBoundsLengths()
    {
        var service = new RadioDiscoveryService();
        var data = Fixture("p2-saturn.hex");
        for (int length = 0; length < 24; length++)
            Assert.IsFalse(service.parseDiscoveryReply(data[..length], length, Radio, Options()).IsDiscovery);
        Assert.IsFalse(service.parseDiscoveryReply(data, data.Length + 1, Radio, Options()).IsDiscovery);
        Assert.IsFalse(service.parseDiscoveryReply(null!, 60, Radio, Options()).IsDiscovery);
        Assert.IsFalse(service.parseDiscoveryReply(new byte[60], 60, Radio, Options()).IsDiscovery);
        data[4] = 4;
        Assert.IsFalse(service.parseDiscoveryReply(data, data.Length, Radio, Options()).IsDiscovery);
    }

    [TestMethod]
    [DataRow(0, HPSDRHW.Atlas)]
    [DataRow(1, HPSDRHW.Hermes)]
    [DataRow(2, HPSDRHW.HermesII)]
    [DataRow(4, HPSDRHW.Angelia)]
    [DataRow(5, HPSDRHW.Orion)]
    [DataRow(10, HPSDRHW.OrionMKII)]
    public void PreservesProtocol1BoardMapping(int board, HPSDRHW expected)
    {
        var data = Fixture("p1-hermes-lite.hex");
        data[10] = (byte)board;
        Assert.AreEqual(expected, new RadioDiscoveryService().parseDiscoveryReply(data, data.Length, Radio, Options()).DeviceType);
    }

    [TestMethod]
    public void ScanDeduplicatesRepliesButRetainsBothProtocols()
    {
        var socket = new FakeSocket();
        socket.Enqueue(Fixture("p1-hermes-lite.hex"), Radio);
        socket.Enqueue(Fixture("p1-hermes-lite.hex"), Radio);
        var p2 = Fixture("p2-saturn.hex");
        // Same address AND MAC: protocol must remain part of the identity key.
        p2[10] = 1;
        socket.Enqueue(p2, Radio);
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, Options(), out var diag);
        Assert.AreEqual(2, radios.Count);
        Assert.AreEqual(1, diag.RejectedDuplicate);
        Assert.AreEqual(2, diag.UniqueRadios);
        Assert.AreEqual(2, socket.Sent.Count);
        Assert.AreEqual(63, socket.Sent[0].Packet.Length);
        Assert.AreEqual(60, socket.Sent[1].Packet.Length);
        CollectionAssert.AreEqual(new byte[] { 0xef, 0xfe, 2 }, socket.Sent[0].Packet[..3]);
        Assert.AreEqual((byte)2, socket.Sent[1].Packet[4]);
        Assert.AreEqual(IPAddress.Parse("192.0.2.255"), socket.Sent[0].Endpoint.Address);
        Assert.AreEqual(Local, socket.BoundTo!.Address);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public void ScanFiltersMalformedZeroMacWrongProtocolSubnetAndTarget()
    {
        var options = Options();
        options.ProtocolMode = RadioDiscoveryProtocolMode.P2Only;
        options.FixedTargetIp = Radio;
        var socket = new FakeSocket();
        socket.Enqueue(new byte[60], Radio);
        var zeroMac = Fixture("p2-saturn.hex");
        Array.Clear(zeroMac, 5, 6);
        socket.Enqueue(zeroMac, Radio);
        socket.Enqueue(Fixture("p1-hermes-lite.hex"), Radio);
        socket.Enqueue(Fixture("p2-saturn.hex"), IPAddress.Parse("198.51.100.20"));
        socket.Enqueue(Fixture("p2-saturn.hex"), IPAddress.Parse("192.0.2.30"));
        socket.Enqueue(Fixture("p2-saturn.hex"), Radio);
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, options, out var diag);
        Assert.AreEqual(1, radios.Count);
        Assert.AreEqual(1, diag.RejectedMalformed);
        Assert.AreEqual(1, diag.RejectedMacInvalid);
        Assert.AreEqual(1, diag.RejectedProtocolModeMismatch);
        Assert.AreEqual(1, diag.RejectedSubnet);
        Assert.AreEqual(1, diag.RejectedFixedTargetMismatch);
        Assert.AreEqual(1, socket.Sent.Count);
        Assert.AreEqual(Radio, socket.Sent[0].Endpoint.Address);
    }

    [TestMethod]
    public void NoReplyCompletesQuietPollsAndDisposesSocket()
    {
        var socket = new FakeSocket();
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, Options(), out var diag);
        Assert.AreEqual(0, radios.Count);
        Assert.AreEqual(2, diag.QuietPolls);
        Assert.IsFalse(diag.SocketError);
        Assert.IsFalse(diag.DeadlineReached);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public void OversizedUnrelatedDatagramDoesNotHideFollowingValidReply()
    {
        var socket = new FakeSocket();
        socket.Enqueue(new byte[4096], Radio);
        socket.Enqueue(Fixture("p2-saturn.hex"), Radio);
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, Options(), out var diag);
        Assert.AreEqual(1, radios.Count);
        Assert.AreEqual(1, diag.RejectedMalformed);
        Assert.IsFalse(diag.SocketError);
    }

    [TestMethod]
    public void InterfaceOptionCopiesDoNotMutateCallerState()
    {
        var options = Options();
        options.FixedLocalIp = Local;
        var copy = options.Copy();
        copy.FixedLocalIp = null;
        copy.ScanPerformance = ScanPerformanceProfile.VeryTolerant;
        Assert.AreEqual(Local, options.FixedLocalIp);
        Assert.AreEqual(ScanPerformanceProfile.UltraFast, options.ScanPerformance);
    }

    [TestMethod]
    public void CustomPortsAndP2UnicastArePreserved()
    {
        var options = Options();
        options.ProtocolMode = RadioDiscoveryProtocolMode.P2Only;
        options.FixedTargetIp = Radio;
        options.DiscoveryPortBase = 15000;
        options.BindLocalPort = 15001;
        var socket = new FakeSocket();
        socket.Enqueue(Fixture("p2-saturn.hex"), Radio);
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, options, out _);
        Assert.AreEqual(15001, socket.BoundTo!.Port);
        Assert.AreEqual(1, socket.Sent.Count);
        Assert.AreEqual(new IPEndPoint(Radio, 15000), socket.Sent[0].Endpoint);
        Assert.AreEqual(15000, radios[0].DiscoveryPortBase);
        Assert.AreEqual(18, radios[0].PortCount);
    }

    [TestMethod]
    [DataRow(SocketError.AccessDenied)]
    [DataRow(SocketError.AddressAlreadyInUse)]
    [DataRow(SocketError.NetworkUnreachable)]
    public void SocketFailuresAreNotReportedAsNoRadio(SocketError failure)
    {
        var socket = new FakeSocket { BindFailure = new SocketException((int)failure) };
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, Options(), out var diag);
        Assert.AreEqual(0, radios.Count);
        Assert.IsTrue(diag.SocketError);
        Assert.AreEqual(failure.ToString(), diag.SocketErrorCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(diag.ErrorMessage));
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public void CancellationDuringPollClosesTheSocketAndPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        var socket = new FakeSocket { OnPoll = cancellation.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, Options(), out _, cancellation.Token));
        Assert.IsTrue(socket.Disposed);
        Assert.IsTrue(socket.LargestPollMicroseconds <= 50_000);
    }

    [TestMethod]
    public void AlreadyCancelledScanDoesNotCreateSocket()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool created = false;
        var service = new RadioDiscoveryService(() => { created = true; return new FakeSocket(); });
        Assert.ThrowsExactly<OperationCanceledException>(() => service.discoverOnNic(Local, Mask, Options(), out _, cancellation.Token));
        Assert.IsFalse(created);
    }

    [TestMethod]
    public void ContinuousMalformedTrafficCannotPreventDeadline()
    {
        var options = Options();
        options.MaxScanMilliseconds = 30;
        var socket = new FakeSocket { RepeatPacket = new byte[60], OnPoll = () => Thread.Sleep(1) };
        var elapsed = Stopwatch.StartNew();
        var radios = new RadioDiscoveryService(() => socket).discoverOnNic(Local, Mask, options, out var diag);
        Assert.AreEqual(0, radios.Count);
        Assert.IsTrue(diag.DeadlineReached);
        Assert.IsTrue(diag.RejectedMalformed > 0);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(2));
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public void ExpiredWholeScanBudgetDoesNotSendOnAnotherInterface()
    {
        var options = Options();
        options.MaxScanMilliseconds = 1;
        var scanClock = Stopwatch.StartNew();
        Thread.Sleep(10);
        bool created = false;
        var service = new RadioDiscoveryService(() => { created = true; return new FakeSocket(); });
        service.discoverOnNic(Local, Mask, options, out var diag, scanClock: scanClock);
        Assert.IsTrue(diag.DeadlineReached);
        Assert.IsFalse(created);
    }

    [TestMethod]
    public void InvalidPortFailsBeforeOpeningSocket()
    {
        var options = Options();
        options.DiscoveryPortBase = 65536;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new RadioDiscoveryService().discoverOnNic(Local, Mask, options, out _));
    }

    private sealed class FakeSocket : IDiscoverySocket
    {
        private readonly Queue<(byte[] Packet, IPAddress Sender)> incoming = new();
        public List<(byte[] Packet, IPEndPoint Endpoint)> Sent { get; } = [];
        public IPEndPoint? BoundTo { get; private set; }
        public bool Disposed { get; private set; }
        public SocketException? BindFailure { get; init; }
        public Action? OnPoll { get; init; }
        public byte[]? RepeatPacket { get; init; }
        public int LargestPollMicroseconds { get; private set; }

        public void Enqueue(byte[] packet, IPAddress sender) => incoming.Enqueue((packet, sender));
        public void Bind(IPEndPoint endpoint)
        {
            BoundTo = endpoint;
            if (BindFailure is not null) throw BindFailure;
        }
        public void SendTo(byte[] packet, IPEndPoint endpoint) => Sent.Add((packet.ToArray(), endpoint));
        public bool Poll(int microseconds)
        {
            LargestPollMicroseconds = Math.Max(LargestPollMicroseconds, microseconds);
            OnPoll?.Invoke();
            return incoming.Count > 0 || RepeatPacket is not null;
        }
        public int ReceiveFrom(byte[] buffer, ref EndPoint remote)
        {
            var (packet, sender) = incoming.Count > 0 ? incoming.Dequeue() : (RepeatPacket!, Radio);
            packet.CopyTo(buffer, 0);
            remote = new IPEndPoint(sender, 1024);
            return packet.Length;
        }
        public void Dispose() => Disposed = true;
    }
}
