using System.Buffers.Binary;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SimulatorTransmitTests
{
    private const int Port = 52000;
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 41000);
    private static void Discard(int offset, byte[] bytes, IPEndPoint target) { }
    internal static byte[] Setup()
    {
        byte[] p = new byte[60]; p[4] = 1; p[15] = 192; p[16] = 24;
        return p;
    }
    internal static byte[] High(bool ptt = true, bool run = true, bool phase = false)
    {
        byte[] p = SimulatorProtocolTests.High(run: run, phase: phase);
        if (ptt) p[4] |= 2;
        p[345] = 127;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(329), phase
            ? (uint)(14_250_000.0 * 4294967296.0 / 122880000.0) : 14_250_000);
        return p;
    }
    internal static byte[] Iq(uint sequence = 0)
    {
        byte[] p = new byte[1444]; BinaryPrimitives.WriteUInt32BigEndian(p, sequence);
        // Independent literal encoding: I=+0.5 (40 00 00), Q=-0.25 (e0 00 00).
        for (int n = 0; n < 240; ++n) { p[4 + 6 * n] = 0x40; p[7 + 6 * n] = 0xe0; }
        return p;
    }
    private static P2Device Device(bool phase = false, bool ptt = true)
    {
        var device = new P2Device(new(BasePort: Port, SimulateTransmit: true));
        device.Accept(0, SimulatorProtocolTests.General(phase: phase), Client, 0, Discard);
        device.Accept(1, SimulatorProtocolTests.Rx(), Client, 0, Discard);
        device.Accept(2, Setup(), Client, 0, Discard);
        device.Accept(3, High(ptt: ptt, phase: phase), Client, 0, Discard);
        Assert.IsTrue(device.Running);
        return device;
    }

    [TestMethod]
    public void TxRequiresOptInAndAValidTransmitHandshake()
    {
        foreach (bool enabled in new[] { false, true })
        {
            var device = new P2Device(new(BasePort: Port, SimulateTransmit: enabled));
            device.Accept(0, SimulatorProtocolTests.General(), Client, 0, Discard);
            device.Accept(1, SimulatorProtocolTests.Rx(), Client, 0, Discard);
            device.Accept(3, High(), Client, 0, Discard);
            Assert.IsFalse(device.Running);
            Assert.IsFalse(device.Snapshot().Configured);
            Assert.IsFalse(device.Snapshot().Transmit.Ptt);
            Assert.AreEqual(1L, device.Snapshot().UnsafeRequests);
        }
        var rxOnly = new P2Device(new(BasePort: Port));
        rxOnly.Accept(0, SimulatorProtocolTests.General(), Client, 0, Discard);
        rxOnly.Accept(5, Iq(), Client, 0, Discard);
        Assert.IsFalse(rxOnly.Snapshot().Configured);
        Assert.AreEqual(1L, rxOnly.Snapshot().UnsafeRequests);
    }

    [TestMethod]
    public void LiteralTxPayloadHas240SamplesAndMeasuredComplexLevels()
    {
        var device = Device(); device.Accept(5, Iq(), Client, 0, Discard);
        var tx = device.Snapshot().Transmit;
        Assert.IsTrue(tx.Enabled && tx.Configured && tx.Ptt);
        Assert.AreEqual(192000, tx.Rate);
        Assert.AreEqual(14_250_000, tx.FrequencyHz);
        Assert.AreEqual((byte)127, tx.Drive);
        Assert.AreEqual(1L, tx.Packets); Assert.AreEqual(240L, tx.Samples);
        Assert.AreEqual(0.5, tx.MeanI); Assert.AreEqual(-0.25, tx.MeanQ);
        Assert.AreEqual(Math.Sqrt(0.3125), tx.RmsMagnitude, 1e-12);
        Assert.AreEqual(tx.RmsMagnitude, tx.PeakMagnitude);
        Assert.AreEqual(0L, tx.FullScaleComponents);
    }

    [TestMethod]
    public void TxPhaseWordTuningUsesTheSameReferenceAsRx()
    {
        var device = Device(phase: true);
        Assert.AreEqual(14_250_000, device.Snapshot().Transmit.FrequencyHz, 0.03);
    }

    [TestMethod]
    public void FullScaleSignedSamplesDoNotOverflowAndAreCountedPerComponent()
    {
        var device = Device(); var packet = Iq();
        for (int n = 0; n < 240; ++n)
        {
            packet[4 + 6 * n] = 0x80;
            packet[7 + 6 * n] = 0x7f; packet[8 + 6 * n] = packet[9 + 6 * n] = 0xff;
        }
        device.Accept(5, packet, Client, 0, Discard);
        var tx = device.Snapshot().Transmit;
        Assert.AreEqual(480L, tx.FullScaleComponents);
        Assert.AreEqual(-1, tx.MeanI);
        Assert.AreEqual(8388607.0 / 8388608, tx.MeanQ);
        Assert.AreEqual(Math.Sqrt(1 + tx.MeanQ * tx.MeanQ), tx.RmsMagnitude, 1e-12);
    }

    [TestMethod]
    public void SequenceWrapLossDuplicatesAndLatePacketsAreAccountedWithoutDoubleCountingSamples()
    {
        var device = Device();
        foreach (uint sequence in new uint[] { uint.MaxValue - 1, uint.MaxValue, 0, 2, 2, 1, 3 })
            device.Accept(5, Iq(sequence), Client, 0, Discard);
        var tx = device.Snapshot().Transmit;
        Assert.AreEqual(5L, tx.Packets); Assert.AreEqual(1200L, tx.Samples);
        Assert.AreEqual(1L, tx.MissingPackets); Assert.AreEqual(2L, tx.OutOfOrderPackets);
        Assert.AreEqual((uint?)3, tx.LastSequence);
    }

    [TestMethod]
    public void UnkeyedIqIsDiscardedAndPttCyclesDoNotResetTheRunningSequence()
    {
        var device = Device(ptt: false);
        device.Accept(5, Iq(0), Client, 0, Discard);
        device.Accept(3, High(), Client, 0.1, Discard);
        device.Accept(5, Iq(1), Client, 0.1, Discard);
        device.Accept(3, High(ptt: false), Client, 0.2, Discard);
        device.Accept(5, Iq(2), Client, 0.2, Discard);
        device.Accept(3, High(), Client, 0.3, Discard);
        device.Accept(5, Iq(3), Client, 0.3, Discard);
        var tx = device.Snapshot().Transmit;
        Assert.AreEqual(2L, tx.Bursts); Assert.AreEqual(2L, tx.Packets);
        Assert.AreEqual(2L, tx.UnkeyedPackets); Assert.AreEqual(0L, tx.MissingPackets);
        // Run=false always wins, even if the packet still carries PTT=true.
        device.Accept(3, High(run: false), Client, 0.4, Discard);
        Assert.IsFalse(device.Snapshot().Transmit.Ptt);
        device.Accept(3, High(), Client, 0.5, Discard);
        device.Accept(5, Iq(0), Client, 0.5, Discard);
        Assert.AreEqual(1L, device.Snapshot().Transmit.Packets);
        Assert.AreEqual(1L, device.Snapshot().Transmit.Bursts);
    }

    [TestMethod]
    public void MalformedSetupAndIqLeaveAValidSinkUnchanged()
    {
        var device = Device();
        foreach (int field in new[] { 4, 5, 15, 16 })
        {
            var setup = Setup(); setup[field] = field switch { 4 => (byte)2, 5 => (byte)1, 15 => (byte)48, _ => (byte)16 };
            device.Accept(2, setup, Client, 0, Discard);
        }
        device.Accept(2, Setup()[..59], Client, 0, Discard);
        device.Accept(5, Iq()[..1443], Client, 0, Discard);
        device.Accept(5, new byte[65507], Client, 0, Discard);
        Assert.AreEqual(7L, device.Snapshot().RejectedPackets);
        Assert.IsTrue(device.Snapshot().Transmit.Ptt);
        Assert.AreEqual(0L, device.Snapshot().Transmit.Samples);
        var legacy = Setup(); legacy[15] = legacy[16] = 0;
        device.Accept(2, legacy, Client, 0, Discard);
        device.Accept(5, Iq(), Client, 0, Discard);
        Assert.AreEqual(240L, device.Snapshot().Transmit.Samples);
    }

    [TestMethod]
    public void CwAndPureSignalRemainExplicitlyUnsupported()
    {
        foreach (int field in new[] { 4, 5 })
        {
            var device = Device(); var high = High(); high[field] |= field == 4 ? (byte)128 : (byte)1;
            device.Accept(3, high, Client, 0, Discard);
            Assert.IsFalse(device.Snapshot().Configured);
            Assert.IsFalse(device.Snapshot().Transmit.Ptt);
        }
        var cwDevice = Device(); var cw = Setup(); cw[5] = 2;
        cwDevice.Accept(2, cw, Client, 0, Discard);
        Assert.AreEqual(1L, cwDevice.Snapshot().RejectedPackets);
    }

    [TestMethod]
    public void TxDataCannotKeepAnAbandonedControlLeaseAlive()
    {
        var device = Device(); device.Accept(5, Iq(), Client, 1.99, Discard);
        device.Tick(2.01, Discard);
        Assert.IsFalse(device.Snapshot().Configured);
        Assert.IsFalse(device.Snapshot().Transmit.Ptt || device.Snapshot().Transmit.Configured);
        Assert.AreEqual(1L, device.Snapshot().WatchdogStops);
        device.Accept(5, Iq(1), Client, 2.02, Discard);
        Assert.AreEqual(1L, device.Snapshot().Transmit.Packets);
    }

    [TestMethod]
    public void ForeignAndNonloopbackSourcesCannotKeyRetuneOrFeedTheSink()
    {
        var device = Device(ptt: false);
        foreach (var source in new[] { new IPEndPoint(IPAddress.Loopback, 41001), new IPEndPoint(IPAddress.Parse("192.0.2.1"), 41000) })
        {
            device.Accept(0, SimulatorProtocolTests.General(), source, 0, Discard);
            device.Accept(2, Setup(), source, 0, Discard);
            device.Accept(3, High(), source, 0, Discard);
            device.Accept(5, Iq(), source, 0, Discard);
        }
        Assert.AreEqual(8L, device.Snapshot().RejectedPackets);
        Assert.IsFalse(device.Snapshot().Transmit.Ptt);
        Assert.AreEqual(0L, device.Snapshot().Transmit.Packets);
    }

    [TestMethod]
    public void RxContinuesDuringVirtualTxAndStatusNeverEchoesSoftwarePtt()
    {
        var device = Device(); var output = new List<(int Port, byte[] Data)>();
        device.Tick(0, (port, bytes, _) => output.Add((port, (byte[])bytes.Clone())));
        var status = output.Single(p => p.Port == 1).Data;
        Assert.IsTrue(device.Snapshot().Transmit.Ptt);
        Assert.AreEqual(-1, status.AsSpan(4).IndexOfAnyExcept((byte)0));
        Assert.AreEqual(1444, output.Single(p => p.Port == 13).Data.Length);
        output.Clear(); device.Tick(0.0011, (port, bytes, _) => output.Add((port, (byte[])bytes.Clone())));
        Assert.AreEqual(1, output.Count(p => p.Port == 1));
        device.Accept(3, High(ptt: false), Client, 0.002, Discard);
        output.Clear(); device.Tick(0.002, (port, bytes, _) => output.Add((port, (byte[])bytes.Clone())));
        Assert.AreEqual(1, output.Count(p => p.Port == 1));
        output.Clear(); device.Tick(0.01, (port, bytes, _) => output.Add((port, (byte[])bytes.Clone())));
        Assert.AreEqual(0, output.Count(p => p.Port == 1));
    }

    [TestMethod]
    public void DisconnectAndNewOwnershipCannotInheritATransmitConfiguration()
    {
        var device = Device(); device.Accept(5, Iq(), Client, 0, Discard);
        device.Accept(3, High(ptt: false, run: false), Client, 0.1, Discard);
        var next = new IPEndPoint(IPAddress.Loopback, 41001);
        device.Accept(0, SimulatorProtocolTests.General(), next, 0.2, Discard);
        Assert.IsFalse(device.Snapshot().Transmit.Configured || device.Snapshot().Transmit.Ptt);
        Assert.AreEqual(0L, device.Snapshot().Transmit.Packets);
        var other = Device(); other.SocketFailure();
        Assert.IsFalse(other.Snapshot().Transmit.Configured || other.Snapshot().Transmit.Ptt);
        var last = Device(); last.Shutdown();
        Assert.IsFalse(last.Snapshot().Transmit.Ptt);
    }
}
