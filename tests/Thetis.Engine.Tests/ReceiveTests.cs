using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Headless;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class ReceiveTests
{
    private static string NativeDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return directory;
    }

    [TestMethod]
    public void InvalidOptionsAndPreCancellationFailBeforeNativeLoad()
    {
        foreach (string address in new[] { "192.0.2.1", "0.0.0.0", "::1", "127.1", "localhost" })
            Assert.ThrowsExactly<ArgumentException>(() => P2ReceiveSession.Open("unused", new(51024, Address: address)));
        foreach (var options in new P2ReceiveOptions[]
        {
            new(0), new(65516), new(51024, Ddc: -1), new(51024, Ddc: 10), new(51024, InputRate: 768000),
            new(51024, FrequencyHz: -1), new(51024, FrequencyHz: 61_440_001)
        }) Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P2ReceiveSession.Open("unused", options));
        Assert.ThrowsExactly<OperationCanceledException>(() => P2ReceiveSession.Open("unused", new(51024), new(true)));
    }

    [TestMethod, TestCategory("Native")]
    public async Task SimulatorFlowsThroughNativeWdspAndRetunesWithoutTransmit()
    {
        var result = await ReceiveSelfTest.RunAsync(NativeDirectory());
        Assert.IsTrue(result.Passed && result.LoopbackOnly && !result.TransmitAllowed);
        Assert.AreEqual(1000, result.BeforeTuning.ToneHz, 20);
        Assert.AreEqual(1500, result.AfterTuning.ToneHz, 20);
        Assert.AreEqual(0.25 / Math.Sqrt(2), result.BeforeTuning.Rms, 0.005);
        Assert.IsFalse(result.Simulator.Running || result.Simulator.Transmit.Ptt);
        Assert.AreEqual(0, result.Simulator.Transmit.Packets);
        AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public async Task DdcSelectionSampleRatesAndRepeatedActiveShutdownWork()
    {
        string directory = NativeDirectory();
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        foreach (var (ddc, rate) in new[] { (0, 48000), (9, 96000), (2, 192000), (9, 384000) })
        {
            using var session = P2ReceiveSession.Open(directory, new(simulator.BasePort, ddc, rate));
            var measurement = ReceiveSelfTest.Measure(session, 1000);
            Assert.AreEqual(0.25 / Math.Sqrt(2), measurement.Rms, 0.005);
            var state = session.State;
            Assert.AreEqual(ddc, state.Ddc); Assert.AreEqual(rate, state.InputRate);
            Assert.IsTrue(state.IqPackets > 0 && state.AudioProduced > 0);
            Assert.AreEqual(0, state.DspErrors); Assert.AreEqual(0, state.InputOverruns);
            Assert.ThrowsExactly<InvalidOperationException>(() => OfflineRadioSession.Open(directory));
            Assert.ThrowsExactly<InvalidOperationException>(() => LoopbackTransportSession.Open(directory));
            Assert.ThrowsExactly<InvalidOperationException>(() => DspDiagnostics.Run(directory));
            Assert.ThrowsExactly<InvalidOperationException>(() => P2ReceiveSession.Open(directory, new(simulator.BasePort)));
            Assert.ThrowsExactly<ArgumentException>(() => session.ReadAudio(new double[3]));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.Tune(-1));
            session.Dispose(); session.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.State);
            AssertClosed();
            await WaitUntil(() => !simulator.State.Running);
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, state.LocalPort));
        }
        using var offline = OfflineRadioSession.Open(directory); // ownership returns to the old API
    }

    [TestMethod, TestCategory("Native")]
    public async Task LossForeignPacketsAndSlowAudioReaderAreCountedAndBounded()
    {
        await using var simulator = G2Simulator.Open(new(BasePort: 0, DropEvery: 7));
        using var session = P2ReceiveSession.Open(NativeDirectory(), new(simulator.BasePort));
        using var foreign = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = new IPEndPoint(IPAddress.Loopback, session.State.LocalPort);
        for (int i = 0; i < 3; ++i) await foreign.SendAsync(new byte[1444], endpoint);
        await WaitUntil(() => session.State.IqPackets > 100 && session.State.ForeignPackets >= 3 && session.State.AudioDropped > 0);
        var state = session.State;
        Assert.IsTrue(state.MissingPackets > 0 && simulator.State.InjectedDrops > 0);
        Assert.AreEqual(16384, state.AudioQueued);
        Assert.AreEqual(0, state.DspErrors);
        Assert.AreEqual(0, simulator.State.UnsafeRequests);
        double[] audio = new double[32768]; Assert.AreEqual(16384, session.ReadAudio(audio));
        Assert.IsTrue(audio.All(double.IsFinite));
    }

    [TestMethod, TestCategory("Native")]
    public async Task DisappearingPeerStopsTheControlWorkerAndTuneFailsUntilReopen()
    {
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        using var session = P2ReceiveSession.Open(NativeDirectory(), new(simulator.BasePort));
        await WaitUntil(() => session.State.IqPackets > 10);
        await simulator.DisposeAsync();
        await WaitUntil(() => session.State.SocketErrors > 0, 6000);
        Assert.ThrowsExactly<InvalidOperationException>(() => session.Tune(14_198_500));
        long commands = session.State.CommandsSent;
        await Task.Delay(250);
        Assert.AreEqual(commands, session.State.CommandsSent);
        session.Dispose(); AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public async Task StartupCancellationExceptionsAndActiveMeasurementCancellationReleaseOwnership()
    {
        string directory = NativeDirectory();
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        for (int target = 1; target <= 5; ++target)
        {
            using var cancellation = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => P2ReceiveSession.OpenCore(directory, new(simulator.BasePort), cancellation.Token, stage =>
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                if (stage == target) { cancellation.Cancel(); return 1; }
                return 0;
            }));
            AssertClosed();
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => P2ReceiveSession.OpenCore(directory, new(simulator.BasePort), default,
                stage => stage == target ? throw new IOException("injected") : 0));
            Assert.IsInstanceOfType<IOException>(error.InnerException); AssertClosed();
        }
        using (var session = P2ReceiveSession.Open(directory, new(simulator.BasePort)))
        {
            await WaitUntil(() => session.State.IqPackets > 10);
            using var cancellation = new CancellationTokenSource(100);
            Assert.ThrowsExactly<OperationCanceledException>(() => ReceiveSelfTest.Measure(session, 1000, cancellation.Token));
        }
        AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public async Task ConcurrentPullAndDisposeJoinWorkersAndSafeHandleReleasesAbandonedReceiver()
    {
        string directory = NativeDirectory();
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        using var session = P2ReceiveSession.Open(directory, new(simulator.BasePort));
        await WaitUntil(() => session.State.AudioProduced > 0);
        var reader = Task.Run(() =>
        {
            double[] buffer = new double[128];
            for (int i = 0; i < 1000; ++i)
            {
                try { session.ReadAudio(buffer); _ = session.State; }
                catch (ObjectDisposedException) { return; }
            }
        });
        var closer = Task.Run(() => { session.Dispose(); session.Dispose(); });
        await Task.WhenAll(reader, closer).WaitAsync(TimeSpan.FromSeconds(5)); AssertClosed();
        var abandoned = Abandon(directory, simulator.BasePort);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.IsFalse(abandoned.IsAlive); AssertClosed();
        using var reopened = P2ReceiveSession.Open(directory, new(simulator.BasePort));
        await WaitUntil(() => reopened.State.IqPackets > 10);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(string directory, int port) => new(P2ReceiveSession.Open(directory, new(port)));
    private static async Task WaitUntil(Func<bool> condition, int timeout = 5000)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.ElapsedMilliseconds > timeout) Assert.Fail("Timed out waiting for receive state.");
            await Task.Delay(10);
        }
    }
    private static void AssertClosed()
    {
        var state = P2ReceiveSession.ReadNativeState();
        Assert.AreEqual(0, state[1]); Assert.AreEqual(0, state[2]); Assert.AreEqual(0, state[6]);
        var core = OfflineRadioSession.ReadNativeState(); Assert.AreEqual(0, core[1]); Assert.AreEqual(0, core[10]);
        var transport = LoopbackTransportSession.ReadNativeState(); Assert.AreEqual(0, transport[2]); Assert.AreEqual(0, transport[7]);
    }
}
