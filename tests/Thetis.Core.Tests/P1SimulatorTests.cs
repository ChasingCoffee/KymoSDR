using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class P1SimulatorTests
{
    private static byte[] Control(int command)
    {
        byte[] p = new byte[1032]; p[0] = 0xef; p[1] = 0xfe; p[2] = 1; p[3] = 2;
        foreach (int f in new[] {8,520}) p[f] = p[f+1] = p[f+2] = 127;
        p[523] = (byte)command;
        if (command == 4) BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(524),14_199_000);
        return p;
    }
    private static byte[] Run(bool enabled)
    { byte[] p = new byte[64]; p[0] = 0xef; p[1] = 0xfe; p[2] = 4; p[3] = enabled ? (byte)1 : (byte)0; return p; }
    [TestMethod]
    public void BadOptionsAndPreCancellationDoNotBind()
    {
        foreach (int port in new[] {-1,1,1023,65536}) Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1Simulator.Open(new(port)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1Simulator.Open(new(DropEvery:1)));
        Assert.ThrowsExactly<OperationCanceledException>(() => P1Simulator.Open(token:new(true)));
    }
    [TestMethod]
    public async Task StrictReceiveCommandsRejectMoxSamplesAndForeignOwners()
    {
        await using var simulator = P1Simulator.Open();
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var target = new IPEndPoint(IPAddress.Loopback,simulator.Port);
        await client.SendAsync(Run(true),target);
        byte[] mox = Control(4); mox[523] = 5; await client.SendAsync(mox,target);
        byte[] tx = Control(4); tx[531] = 1; await client.SendAsync(tx,target);
        await P1ReceiveSelfTest.WaitUntil(() => simulator.State.UnsafeRequests == 2 && simulator.State.MalformedRequests == 1);
        Assert.IsFalse(simulator.State.Running);
        await client.SendAsync(Control(28),target); await client.SendAsync(Control(4),target); await client.SendAsync(Run(true),target);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = await client.ReceiveAsync(deadline.Token);
        Assert.AreEqual(simulator.Port,received.RemoteEndPoint.Port); Assert.AreEqual(1032,received.Buffer.Length);
        Assert.AreEqual(6,received.Buffer[3]); Assert.AreEqual(uint.MaxValue-1,BinaryPrimitives.ReadUInt32BigEndian(received.Buffer.AsSpan(4)));
        Assert.AreEqual(127,received.Buffer[520]); Assert.AreEqual(0x7f,received.Buffer[22]); Assert.AreEqual(0xff,received.Buffer[23]);
        await stranger.SendAsync(Run(false),target);
        await P1ReceiveSelfTest.WaitUntil(() => simulator.State.ForeignRequests == 1);
        Assert.IsTrue(simulator.State.Running);
        await client.SendAsync(Run(false),target);
        await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running && simulator.State.Stops == 1);
        Assert.AreEqual(0,simulator.State.SocketErrors);
    }
    [TestMethod]
    public async Task SignalLevelWireStepPreservesPhaseAndResetsOnRestart()
    {
        await using var simulator = P1Simulator.Open(new(Amplitude:.005,SignalLevels:new(new SignalLevelStep(1,.25))));
        var target = new IPEndPoint(IPAddress.Loopback,simulator.Port);
        for (int run = 0; run < 2; ++run)
        {
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
            await client.SendAsync(Control(28),target); await client.SendAsync(Control(4),target); await client.SendAsync(Run(true),target);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var result = await client.ReceiveAsync(deadline.Token);
            Assert.AreEqual(uint.MaxValue-1,BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.AsSpan(4)));
            for (int sample = 0; sample < 126; ++sample)
            {
                int at = 16+512*(sample/63)+8*(sample%63);
                double amplitude = sample < 48 ? .005 : .25, phase = 2*Math.PI*1000*sample/48000;
                Assert.AreEqual(amplitude*8388608*Math.Cos(phase),SimulatorProtocolTests.Read24(result.Buffer.AsSpan(at)),1.1);
                Assert.AreEqual(amplitude*8388608*Math.Sin(phase),SimulatorProtocolTests.Read24(result.Buffer.AsSpan(at+3)),1.1);
            }
            await client.SendAsync(Run(false),target);
            await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running && simulator.State.Stops == run+1);
        }
        Assert.AreEqual(0,simulator.State.UnsafeRequests); Assert.AreEqual(0,simulator.State.SocketErrors);
    }
    [TestMethod]
    public async Task LeaseExpiryAndConcurrentDisposalReleaseThePort()
    {
        await using var simulator = P1Simulator.Open(); int port = simulator.Port;
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var target = new IPEndPoint(IPAddress.Loopback,port);
        await client.SendAsync(Control(28),target); await client.SendAsync(Control(4),target); await client.SendAsync(Run(true),target);
        await P1ReceiveSelfTest.WaitUntil(() => simulator.State.WatchdogStops == 1,seconds:4);
        Assert.IsFalse(simulator.State.Running);
        await Task.WhenAll(simulator.DisposeAsync().AsTask(),simulator.DisposeAsync().AsTask());
        Assert.IsTrue(simulator.Completion.IsCompletedSuccessfully);
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
    }
}
