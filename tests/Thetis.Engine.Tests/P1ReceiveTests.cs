using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class P1ReceiveTests
{
    private static string NativeDirectory()
    {
        string? path = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return path;
    }
    private static void Closed()
    {
        var s = ReceiveSession.ReadNativeState();
        Assert.AreEqual(0, s[1]); Assert.AreEqual(0, s[2]); Assert.AreEqual(0, s[6]);
        Assert.AreEqual(0, OfflineRadioSession.ReadNativeState()[10]);
    }
    [TestMethod]
    public void InvalidAndCancelledOptionsCannotLoadNativeOrReachHardware()
    {
        foreach (string address in new[] { "192.0.2.1", "0.0.0.0", "localhost", "::1", "127.1" })
            Assert.ThrowsExactly<ArgumentException>(() => P1ReceiveSession.Open("unused", new(51024, Address: address)));
        foreach (int port in new[] { 0, 1023, 65536 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1ReceiveSession.Open("unused", new(port)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1ReceiveSession.Open("unused", new(51024, -1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1ReceiveSession.Open("unused", new(51024, Demodulation: new((ReceiveMode)2))));
        Assert.ThrowsExactly<OperationCanceledException>(() => P1ReceiveSession.Open("unused", new(65535), new(true)));
    }
    [TestMethod, TestCategory("Native")]
    public async Task BothSidebandsFiltersRetuneAndReconnectHaveMeasuredSignal()
    {
        var result = await P1ReceiveSelfTest.RunAsync(NativeDirectory());
        Assert.IsTrue(result.Passed && result.LoopbackOnly && !result.TransmitAllowed);
        Assert.AreEqual(14, result.Checks.Count); Assert.IsTrue(result.Checks.All(c => c.Passed));
        Assert.AreEqual(4, result.Spectra.Count); Assert.AreEqual(2, result.Sessions.Count);
        Assert.AreEqual(2, result.Simulator.Stops); Closed();
    }
    [TestMethod, TestCategory("Native")]
    public async Task StartupCancellationAndCallbackFailureRollBackEveryStage()
    {
        string directory = NativeDirectory();
        await using var simulator = P1Simulator.Open();
        for (int target = 1; target <= 5; ++target)
        {
            int at = target;
            using var cancel = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => P1ReceiveSession.OpenCore(directory, new(simulator.Port), cancel.Token,
                stage => { if (stage == at) { cancel.Cancel(); return 1; } return 0; }));
            Closed(); await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running);
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => P1ReceiveSession.OpenCore(directory, new(simulator.Port), default,
                stage => stage == at ? throw new IOException("injected callback") : 0));
            Assert.IsInstanceOfType<IOException>(ex.InnerException);
            Closed(); await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running);
        }
        using var session = P1ReceiveSession.Open(directory, new(simulator.Port));
        Assert.AreEqual(.25/Math.Sqrt(2), ReceiveSelfTest.Measure(session,1000).Rms, .005);
    }
    [TestMethod, TestCategory("Native")]
    public async Task PacketFaultsAndPeerLossAreBoundedAndReconnectResetsCounters()
    {
        string directory = NativeDirectory();
        await using var simulator = P1Simulator.Open(new(DropEvery: 17, InjectFaults: true));
        int port = simulator.Port;
        using var session = P1ReceiveSession.Open(directory, new(port));
        double[] audio = new double[4096];
        await P1ReceiveSelfTest.WaitUntil(() => { session.ReadAudio(audio); return session.State.MissingPackets >= 10; });
        var s = session.State;
        Assert.AreEqual(1, s.MalformedPackets); Assert.AreEqual(1, s.ForeignPackets); Assert.AreEqual(1, s.LatePackets);
        Assert.IsTrue(s.MissingPackets > 0 && simulator.State.InjectedDrops > 0);
        Assert.AreEqual(0, s.DspErrors); Assert.AreEqual(0, s.InputOverruns);
        Assert.ThrowsExactly<InvalidOperationException>(() => P2ReceiveSession.Open(directory, new(port)));
        Assert.ThrowsExactly<InvalidOperationException>(() => OfflineRadioSession.Open(directory));
        Assert.ThrowsExactly<InvalidOperationException>(() => LoopbackTransportSession.Open(directory));
        await simulator.DisposeAsync();
        await P1ReceiveSelfTest.WaitUntil(() => { session.ReadAudio(audio); return session.State.SocketErrors > 0; }, seconds: 5);
        Assert.ThrowsExactly<InvalidOperationException>(() => session.Tune(14_198_500));
        long commands = session.State.CommandsSent;
        await Task.Delay(300); Assert.AreEqual(commands, session.State.CommandsSent);
        int local = session.State.LocalPort; session.Dispose(); Closed();
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, local));
        await using var replacement = P1Simulator.Open(new(port));
        using var reopened = P1ReceiveSession.Open(directory, new(port));
        Assert.AreEqual(.25/Math.Sqrt(2), ReceiveSelfTest.Measure(reopened,1000).Rms,.005);
        P1ReceiveSelfTest.RequireClean(reopened.State);
    }
    [TestMethod, TestCategory("Native")]
    public async Task SlowReadersCancellationAndConcurrentDisposeReleaseTheSharedOwner()
    {
        string directory = NativeDirectory();
        await using var simulator = P1Simulator.Open();
        using var session = P1ReceiveSession.Open(directory,new(simulator.Port));
        await P1ReceiveSelfTest.WaitUntil(() => session.State.AudioProduced >= 48000);
        Assert.IsTrue(session.State.AudioDropped > 0 && session.ReadSpectrum()?.CoalescedFrames > 0);
        Assert.IsTrue(session.State.AudioQueued <= 16384);
        Assert.ThrowsExactly<OperationCanceledException>(() => ReceiveControlsSelfTest.MeasureSettled(session,new(true)));
        await Task.WhenAll(Task.Run(session.Dispose),Task.Run(session.Dispose)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.ReadSpectrum());
        Closed(); await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running);
        await using var p2 = G2Simulator.Open(new(BasePort:0));
        using var other = P2ReceiveSession.Open(directory,new(p2.BasePort));
        Assert.ThrowsExactly<InvalidOperationException>(() => P1ReceiveSession.Open(directory,new(simulator.Port)));
    }
    [TestMethod, TestCategory("Native")]
    public async Task AbandonedHandleStopsAndJoinsP1()
    {
        string directory = NativeDirectory();
        await using var simulator = P1Simulator.Open();
        var weak = Abandon(directory,simulator.Port);
        for (int i = 0; i < 10 && weak.IsAlive; ++i) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(20); }
        Assert.IsFalse(weak.IsAlive); Closed();
        await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(string directory,int port)
    { var session = P1ReceiveSession.Open(directory,new(port)); return new(session); }
}
