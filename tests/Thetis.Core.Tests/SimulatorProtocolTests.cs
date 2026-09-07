using System.Buffers.Binary;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SimulatorProtocolTests
{
    private const int Port = 52000;
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 41000);
    private readonly List<(int Port, byte[] Data)> output = [];
    private void Capture(int offset, byte[] bytes, IPEndPoint target)
    { Assert.AreEqual(Client, target); output.Add((offset, (byte[])bytes.Clone())); }
    private static void Discard(int offset, byte[] bytes, IPEndPoint target) { }
    private static P2Device Device(SimulatorOptions? options = null) => new(options ?? new(BasePort: Port));

    internal static byte[] General(int port = Port, bool phase = false)
    {
        byte[] packet = new byte[60]; packet[37] = phase ? (byte)8 : (byte)0;
        foreach (var (field, offset) in new[] { (5, 1), (7, 2), (9, 3), (11, 1), (13, 4), (15, 5), (17, 11), (19, 2) })
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(field), (ushort)(port + offset));
        return packet;
    }
    internal static byte[] Rx(int ddc = 2, int rate = 48000)
    {
        byte[] packet = new byte[1444]; packet[4] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7), (ushort)(1 << ddc));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18 + 6 * ddc), (ushort)(rate / 1000));
        packet[22 + 6 * ddc] = 24;
        return packet;
    }
    internal static byte[] High(bool run = true, int frequency = 14_199_000, bool phase = false)
    {
        byte[] packet = new byte[1444]; packet[4] = run ? (byte)1 : (byte)0;
        uint word = phase ? (uint)(frequency * (4294967296.0 / 122880000.0)) : (uint)frequency;
        for (int i = 0; i < 10; ++i) BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(9 + 4 * i), word);
        return packet;
    }
    private static void Configure(P2Device device, int ddc = 2, int rate = 48000, bool phase = false)
    {
        device.Accept(0, General(phase: phase), Client, 0, Discard);
        device.Accept(1, Rx(ddc, rate), Client, 0, Discard);
        device.Accept(3, High(phase: phase), Client, 0, Discard);
        Assert.IsTrue(device.Running);
    }
    internal static int Read24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] * 65536 + bytes[1] * 256 + bytes[2];
        return (value & 0x800000) == 0 ? value : value - 0x1000000;
    }

    [TestMethod]
    public void DiscoveryHasDistinctSyntheticIdentityAndIsParsedByExistingClient()
    {
        var device = Device();
        byte[] request = new byte[60]; request[4] = 2;
        device.Accept(0, request, Client, 0, Capture);
        var data = output.Single().Data;
        Assert.AreEqual(60, data.Length);
        var parsed = new RadioDiscoveryService().parseDiscoveryReply(data, data.Length, IPAddress.Loopback, new RadioDiscoveryOptions());
        Assert.IsTrue(parsed.IsDiscovery);
        Assert.AreEqual(HPSDRHW.Saturn, parsed.DeviceType);
        Assert.AreEqual(RadioDiscoveryRadioProtocol.P2, parsed.Protocol);
        Assert.AreEqual("02-4B-59-4D-4F-01", parsed.MacAddress);
        Assert.AreEqual((byte)43, parsed.ProtocolSupported);
        Assert.AreEqual((byte)27, parsed.CodeVersion);
        Assert.AreEqual((byte)10, parsed.NumRxs);
        Assert.IsFalse(parsed.IsBusy);
        Configure(device);
        output.Clear(); device.Accept(0, request, Client, 0.1, Capture);
        Assert.AreEqual((byte)3, output.Single().Data[4]);
    }

    [TestMethod]
    public void FramingSequenceAndToneAreCorrectAcrossPacketBoundaries()
    {
        var device = Device(); Configure(device);
        device.Tick(0, Capture); device.Tick(238.0 / 48000 + 0.000001, Capture);
        var packets = output.Where(p => p.Port == 13).Select(p => p.Data).ToArray();
        Assert.HasCount(2, packets);
        for (int packet = 0; packet < 2; ++packet)
        {
            var bytes = packets[packet];
            Assert.AreEqual(1444, bytes.Length);
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, (byte)packet, 0, 0, 0, 0, 0, 0, 0, 0, 0, 24, 0, 238 }, bytes[..16]);
            for (int sample = 0; sample < 238; ++sample)
            {
                double phase = 2 * Math.PI * (238 * packet + sample) / 48;
                Assert.AreEqual(2097152 * Math.Cos(phase), Read24(bytes.AsSpan(16 + 6 * sample)), 1.0);
                Assert.AreEqual(2097152 * Math.Sin(phase), Read24(bytes.AsSpan(19 + 6 * sample)), 1.0);
            }
        }
        Assert.IsTrue(output.Any(p => p.Port == 2 && p.Data.Length == 132 && p.Data.AsSpan(4).IndexOfAnyExcept((byte)0) < 0));
        Assert.IsTrue(output.Any(p => p.Port == 1 && p.Data.Length == 60 && p.Data.AsSpan(4).IndexOfAnyExcept((byte)0) < 0));
    }

    [TestMethod]
    [DataRow(48000)] [DataRow(96000)] [DataRow(192000)] [DataRow(384000)]
    public void RatesAndPhaseWordTuningAreHonored(int rate)
    {
        var device = Device(); Configure(device, ddc: 9, rate: rate, phase: true);
        var r = device.Snapshot().Receivers[9];
        Assert.AreEqual(rate, r.Rate);
        Assert.AreEqual(14_199_000, r.FrequencyHz, 0.03);
        device.Tick(0, Capture);
        var packet = output.Single(p => p.Port == 20).Data;
        double angle = Math.Atan2(Read24(packet.AsSpan(25)), Read24(packet.AsSpan(22)));
        Assert.AreEqual(1000, angle * rate / (2 * Math.PI), 0.1);
    }

    [TestMethod]
    public void TuningOutOfBandDoesNotAliasTheSyntheticStationIntoThePassband()
    {
        var device = Device(); Configure(device);
        device.Accept(3, High(frequency: 7_100_000), Client, 0, Discard);
        device.Tick(0, Capture);
        Assert.AreEqual(-1, output.Single(p => p.Port == 13).Data.AsSpan(16).IndexOfAnyExcept((byte)0));
    }

    [TestMethod]
    public void SeededNoiseIsRepeatableAndDropFaultsPreserveSequenceGaps()
    {
        var options = new SimulatorOptions(BasePort: Port, Amplitude: 0, NoiseAmplitude: 0.01, Seed: 123, DropEvery: 2);
        var a = Device(options); var b = Device(options);
        Configure(a); Configure(b);
        for (int i = 0; i < 5; ++i) a.Tick(i * 238.0 / 48000 + 0.000001, Capture);
        var expected = output.Where(p => p.Port == 13).Select(p => p.Data).ToArray(); output.Clear();
        for (int i = 0; i < 5; ++i) b.Tick(i * 238.0 / 48000 + 0.000001, Capture);
        var actual = output.Where(p => p.Port == 13).Select(p => p.Data).ToArray();
        Assert.HasCount(3, actual);
        for (int i = 0; i < 3; ++i)
        {
            Assert.AreEqual((uint)(i * 2), BinaryPrimitives.ReadUInt32BigEndian(actual[i]));
            CollectionAssert.AreEqual(expected[i], actual[i]);
        }
        Assert.AreNotEqual(0, Read24(actual[0].AsSpan(16)));
        Assert.AreEqual(2L, b.Snapshot().InjectedDrops);
    }

    [TestMethod]
    public void StopRestartAndWatchdogReleaseTheClientLease()
    {
        var device = Device(); Configure(device); device.Tick(0, Capture);
        device.Accept(3, High(run: false), Client, 0.1, Discard);
        output.Clear(); device.Tick(0.2, Capture); Assert.IsEmpty(output);
        device.Accept(3, High(), Client, 0.3, Discard); device.Tick(0.3, Capture);
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(output.Single(p => p.Port == 13).Data));
        output.Clear(); device.Tick(2.31, Capture); Assert.IsEmpty(output);
        Assert.IsFalse(device.Snapshot().Configured);
        Assert.AreEqual(1L, device.Snapshot().WatchdogStops);
        Assert.AreEqual(2L, device.Snapshot().Starts);
        var second = new IPEndPoint(IPAddress.Loopback, 41001);
        device.Accept(0, General(), second, 3, Discard);
        Assert.AreEqual(second.ToString(), device.Snapshot().Client);
    }

    [TestMethod]
    public void OtherClientsCannotRetuneOrKeepAliveAnOwnedSession()
    {
        var device = Device(); Configure(device);
        var intruder = new IPEndPoint(IPAddress.Loopback, 41001);
        device.Accept(3, High(frequency: 7100000), intruder, 1.5, Discard);
        Assert.AreEqual(14_199_000, device.Snapshot().Receivers[2].FrequencyHz);
        device.Tick(2.01, Capture); Assert.IsFalse(device.Running);
        Assert.AreEqual(1L, device.Snapshot().RejectedPackets);
    }

    [TestMethod]
    public void AStoppedDeviceCanBeClaimedImmediatelyByANewClient()
    {
        var device = Device(); Configure(device);
        var second = new IPEndPoint(IPAddress.Loopback, 41001);
        device.Accept(0, General(), second, 0.1, Discard);
        Assert.AreEqual(Client.ToString(), device.Snapshot().Client);
        device.Accept(3, High(run: false), Client, 0.2, Discard);
        device.Accept(0, General(), second, 0.3, Discard);
        Assert.AreEqual(second.ToString(), device.Snapshot().Client);
        Assert.IsTrue(device.Snapshot().Receivers.All(r => !r.Enabled));
        device.Accept(3, High(), second, 0.4, Discard); // must configure receivers again
        Assert.IsFalse(device.Running);
    }

    [TestMethod]
    public void MultipleDdcsHaveIndependentPortsRatesAndSequenceCounters()
    {
        var device = Device();
        device.Accept(0, General(), Client, 0, Discard);
        var rx = Rx(2);
        rx[7] |= 1 << 5; rx[8] |= 1 << 1;
        rx[49] = 96; rx[52] = 24; rx[73] = 192; rx[76] = 24;
        device.Accept(1, rx, Client, 0, Discard);
        device.Accept(3, High(), Client, 0, Discard);
        device.Tick(0, Capture); device.Tick(238.0 / 48000 + 0.000001, Capture);
        foreach (var (offset, count) in new[] { (13, 2), (16, 3), (20, 5) })
        {
            var packets = output.Where(p => p.Port == offset).ToArray();
            Assert.HasCount(count, packets);
            for (int i = 0; i < count; ++i) Assert.AreEqual((uint)i, BinaryPrimitives.ReadUInt32BigEndian(packets[i].Data));
        }
        Assert.IsFalse(output.Any(p => p.Port == 11 || p.Port == 12));
    }

    [TestMethod]
    public void InvalidPacketsAndUnsupportedModesDoNotChangeValidConfiguration()
    {
        var device = Device(); Configure(device);
        device.Accept(1, new byte[65507], Client, 0.1, Discard);
        device.Accept(1, Rx()[..1443], Client, 0.1, Discard);
        var sync = Rx(); sync[1365] = 8; device.Accept(1, sync, Client, 0.1, Discard);
        var rate = Rx(rate: 768000); device.Accept(1, rate, Client, 0.1, Discard);
        var size = Rx(); size[34] = 16; device.Accept(1, size, Client, 0.1, Discard);
        var adc = Rx(); adc[29] = 2; device.Accept(1, adc, Client, 0.1, Discard);
        var vita = General(); vita[37] = 2; device.Accept(0, vita, Client, 0.1, Discard);
        var ports = General(); ports[5] = ports[6] = 0; device.Accept(0, ports, Client, 0.1, Discard);
        Assert.AreEqual(8L, device.Snapshot().RejectedPackets);
        Assert.AreEqual(48000, device.Snapshot().Receivers[2].Rate);
        Assert.IsTrue(device.Running);
    }

    [TestMethod]
    public void NonLoopbackAndSelfAddressedTrafficIsRejected()
    {
        var device = Device();
        device.Accept(0, General(), new IPEndPoint(IPAddress.Parse("192.0.2.1"), 41000), 0, Discard);
        device.Accept(0, General(), new IPEndPoint(IPAddress.Loopback, Port + 13), 0, Discard);
        Assert.IsFalse(device.Snapshot().Configured);
        Assert.AreEqual(2L, device.Snapshot().RejectedPackets);
    }

    [TestMethod]
    public void UnsafePttOrKeyingIsRejectedAndNeedsANewHandshake()
    {
        foreach (int field in new[] { 4, 5 })
        {
            var device = Device(); Configure(device);
            var bad = High(); bad[field] |= 2;
            device.Accept(3, bad, Client, 0.1, Discard);
            device.Accept(3, High(), Client, 0.2, Discard);
            device.Tick(0.3, Capture);
            Assert.IsEmpty(output);
            Assert.IsFalse(device.Running);
            Assert.IsFalse(device.Snapshot().Configured);
            Assert.AreEqual(1L, device.Snapshot().UnsafeRequests);
        }
    }

    [TestMethod]
    public void Signed24BitBoundaryEncodingMatchesLiteralBytes()
    {
        byte[] buffer = new byte[3];
        P2Device.Write24(buffer, -1); CollectionAssert.AreEqual(new byte[] { 0x80, 0, 0 }, buffer);
        P2Device.Write24(buffer, 1); CollectionAssert.AreEqual(new byte[] { 0x7f, 0xff, 0xff }, buffer);
        P2Device.Write24(buffer, -1.0 / 8388608); CollectionAssert.AreEqual(new byte[] { 0xff, 0xff, 0xff }, buffer);
    }

    [TestMethod]
    public void LongSchedulerStallsDoNotCauseUnboundedCatchUpBursts()
    {
        var device = Device(new(BasePort: Port, LeaseTimeoutMilliseconds: 60000)); Configure(device, rate: 384000);
        device.Tick(10, Capture);
        Assert.AreEqual(32, output.Count(p => p.Port == 13));
        Assert.IsTrue(device.Snapshot().PacingResyncs > 0);
    }
}
