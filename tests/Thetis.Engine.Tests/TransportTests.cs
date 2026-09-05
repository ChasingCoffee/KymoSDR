using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class TransportTests
{
    private static string NativeDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return directory;
    }

    [TestMethod]
    public void HardwareAddressesAndInvalidOptionsAreRejectedBeforeNativeLoad()
    {
        foreach (var options in new LoopbackTransportOptions[]
        {
            new(RemoteAddress: "192.0.2.1"), new(LocalAddress: "0.0.0.0"), new(RemoteAddress: "::1"),
            new(RemoteAddress: "127.1"), new(Protocol: (ProbeProtocol)9), new(Model: (ProbeRadioModel)10),
            new(Protocol: ProbeProtocol.P1, Model: ProbeRadioModel.HermesLite, UseRelocatedPorts: true)
        }) Assert.ThrowsExactly<ArgumentException>(() => LoopbackTransportSession.Open("unused", options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoopbackTransportSession.Open("unused", new(RemotePort: 65519)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LoopbackTransportSession.Open("unused", new(LocalPort: -1)));
        Assert.ThrowsExactly<OperationCanceledException>(() => LoopbackTransportSession.Open("unused", cancellationToken: new(true)));
    }

    [TestMethod, TestCategory("Native")]
    public void LoopbackFixturesAreDiscardedWithoutRepliesAndDisposeJoinsWorkers()
    {
        string directory = NativeDirectory();
        using var peer = TransportDiagnostics.OpenPeer();
        int peerPort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
        using var session = LoopbackTransportSession.Open(directory, new(RemotePort: peerPort));
        var endpoint = new IPEndPoint(IPAddress.Loopback, session.State.LocalPort);
        Assert.ThrowsExactly<InvalidOperationException>(() => LoopbackTransportSession.Open(directory));
        Assert.ThrowsExactly<InvalidOperationException>(() => OfflineRadioSession.Open(directory));
        Assert.ThrowsExactly<InvalidOperationException>(() => DspDiagnostics.Run(directory));
        foreach (int size in new[] { 0, 32, 1444, 2048 }) peer.Send(new byte[size], size, endpoint);
        GC.Collect(); GC.WaitForPendingFinalizers();
        var state = TransportDiagnostics.WaitForFixtures(session, default);
        Assert.AreEqual(1476, state.ReceivedBytes);
        Assert.AreEqual(2, state.Workers);
        Assert.AreEqual(0, peer.Available);
        session.Dispose(); session.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.State);
        AssertClosed();
        using var rebound = new UdpClient(endpoint);
    }

    [TestMethod, TestCategory("Native")]
    public void ActualSocketInitializationPreservesProtocolModelAndP2PortChoice()
    {
        string directory = NativeDirectory();
        foreach (var (port, relocate, expected) in new[] { (1024, false, 1025), (1024, true, 1025), (5000, false, 1025), (5000, true, 5001) })
        {
            using var session = LoopbackTransportSession.Open(directory, new(RemotePort: port, UseRelocatedPorts: relocate));
            Assert.AreEqual(expected, session.State.P2PortBase);
            Assert.AreEqual(port, session.State.RemotePort);
            Assert.AreEqual(ProbeProtocol.P2, session.State.Protocol);
            Assert.AreEqual(ProbeRadioModel.G2, session.State.Model);
        }
        using var p1 = LoopbackTransportSession.Open(directory, new(Protocol: ProbeProtocol.P1, Model: ProbeRadioModel.HermesLite));
        Assert.AreEqual(ProbeProtocol.P1, p1.State.Protocol);
        Assert.AreEqual(ProbeRadioModel.HermesLite, p1.State.Model);
    }

    [TestMethod, TestCategory("Native")]
    public void CancellationAndCallbackExceptionsRollbackAllFiveStages()
    {
        string directory = NativeDirectory();
        for (int target = 1; target <= 5; ++target)
        {
            using var cts = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => LoopbackTransportSession.OpenCore(directory, null, cts.Token, stage =>
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                if (stage == target) { cts.Cancel(); return 1; }
                return 0;
            }));
            AssertClosed();
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => LoopbackTransportSession.OpenCore(directory, null, default,
                stage => stage == target ? throw new IOException("injected failure") : 0));
            Assert.IsInstanceOfType<IOException>(error.InnerException);
            AssertClosed();
            using var reopened = LoopbackTransportSession.Open(directory);
            Assert.AreEqual(2, reopened.State.Workers);
        }
    }

    [TestMethod, TestCategory("Native")]
    public void PortConflictRollsBackAndTheSamePortCanBeReopened()
    {
        string directory = NativeDirectory();
        using var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)occupied.Client.LocalEndPoint!).Port;
        Assert.ThrowsExactly<InvalidOperationException>(() => LoopbackTransportSession.Open(directory, new(LocalPort: port)));
        AssertClosed();
        occupied.Dispose();
        using var reopened = LoopbackTransportSession.Open(directory, new(LocalPort: port));
        Assert.AreEqual(port, reopened.State.LocalPort);
    }

    [TestMethod, TestCategory("Native")]
    public void ConcurrentDisposeAndStateAccessAreSafe()
    {
        using var session = LoopbackTransportSession.Open(NativeDirectory());
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 1000; ++i)
            {
                try { Assert.AreEqual(2, session.State.Workers); }
                catch (ObjectDisposedException) { return; }
            }
        });
        var closer = Task.Run(() => { session.Dispose(); session.Dispose(); });
        Assert.IsTrue(Task.WaitAll([reader, closer], TimeSpan.FromSeconds(5)));
        AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public void SafeHandleFallbackReleasesAnAbandonedProbe()
    {
        string directory = NativeDirectory();
        var owner = Abandon(directory);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(owner.IsAlive);
        AssertClosed();
        using var reopened = LoopbackTransportSession.Open(directory);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(string directory) => new(LoopbackTransportSession.Open(directory));

    private static void AssertClosed()
    {
        var state = LoopbackTransportSession.ReadNativeState();
        Assert.AreEqual(0, state[1]); Assert.AreEqual(0, state[2]);
        Assert.AreEqual(0, state[7]); Assert.AreEqual(0, state[8]);
        Assert.AreEqual(0, state[15]);
    }
}
