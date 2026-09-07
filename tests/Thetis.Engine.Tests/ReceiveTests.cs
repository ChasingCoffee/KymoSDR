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
    public async Task ShortSoakExercisesFaultsAndReconnectsWithIsolatedCounters()
    {
        var result = await ReceiveSoak.RunAsync(NativeDirectory(), new(10, 1));
        Assert.IsTrue(result.Passed, result.Failure);
        Assert.IsFalse(result.Cancelled || result.TransmitAllowed);
        Assert.AreEqual(1, result.ReconnectsCompleted); Assert.AreEqual(5, result.Phases.Count);
        Assert.IsTrue(result.Phases.All(p => p.Passed && p.NativeDisposed && p.PortRebound));
        Assert.IsTrue(result.Phases[0].ObservedSeconds >= 10 && result.Phases[0].Retunes > 0);
        Assert.IsTrue(result.Phases[0].AudioSignalChecks > 0 && result.Phases[0].SpectrumSignalChecks > 0);
        Assert.IsTrue(result.Phases[0].Resources!.Samples >= 3);
        Assert.IsTrue(result.Phases[1].Native!.MissingPackets > 0 && result.Phases[1].Simulator!.InjectedDrops > 0);
        Assert.IsTrue(result.Phases[2].Native!.AudioDropped > 0 && result.Phases[2].SpectrumFramesCoalesced > 0);
        Assert.IsTrue(result.Phases[3].ExpectedPeerFailureObserved && !result.Phases[3].StopObserved);
        Assert.IsTrue(result.Phases[4].StopObserved);
        Assert.AreEqual(result.Phases[3].Native!.BasePort, result.Phases[4].Native!.BasePort);
        Assert.AreEqual(0, result.Phases[4].Native!.MissingPackets);
        Assert.AreEqual(0, result.Phases[4].Native!.AudioDropped);
        AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public async Task SoakCancellationAndObservationFailureRetainPartialReportAndCloseOwners()
    {
        string directory = NativeDirectory();
        using var cancelled = new CancellationTokenSource();
        var result = await ReceiveSoak.RunAsync(directory, new(10, 1), _ => cancelled.Cancel(), cancelled.Token);
        Assert.IsFalse(result.Passed); Assert.IsTrue(result.Cancelled);
        Assert.AreEqual(1, result.Phases.Count);
        Assert.IsTrue(result.Phases[0].NativeDisposed && result.Phases[0].StopObserved && result.Phases[0].PortRebound);
        AssertClosed();
        result = await ReceiveSoak.RunAsync(directory, new(10, 1), _ => throw new IOException("injected observer error"));
        Assert.IsFalse(result.Passed || result.Cancelled); StringAssert.Contains(result.Failure, "injected observer error");
        Assert.IsTrue(result.Phases[0].NativeDisposed && result.Phases[0].StopObserved && result.Phases[0].PortRebound);
        AssertClosed();
    }

    [TestMethod, TestCategory("Native")]
    public async Task SimulatorFlowsThroughNativeWdspAndRetunesWithoutTransmit()
    {
        var result = await ReceiveSelfTest.RunAsync(NativeDirectory());
        Assert.IsTrue(result.Passed && result.LoopbackOnly && !result.TransmitAllowed);
        Assert.AreEqual(1000, result.BeforeTuning.ToneHz, 20);
        Assert.AreEqual(1500, result.AfterTuning.ToneHz, 20);
        Assert.AreEqual(0.25 / Math.Sqrt(2), result.BeforeTuning.Rms, 0.005);
        Assert.AreEqual(2, result.SchemaVersion);
        Assert.AreEqual(14_200_000, result.SpectrumBeforeTuning.PeakFrequencyHz, result.SpectrumBeforeTuning.BinWidthHz);
        Assert.AreEqual(14_200_000, result.SpectrumAfterTuning.PeakFrequencyHz, result.SpectrumAfterTuning.BinWidthHz);
        Assert.AreEqual(2, result.SpectrumAfterTuning.TuningGeneration);
        Assert.IsTrue(result.SpectrumAfterTuning.Sequence > result.SpectrumBeforeTuning.Sequence);
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
            var spectrum = ReceiveSelfTest.MeasureSpectrum(session, 14_199_000, 14_200_000, 1);
            Assert.AreEqual(rate / 4096.0, spectrum.BinWidthHz);
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
            Assert.ThrowsExactly<ObjectDisposedException>(() => session.ReadSpectrum());
            AssertClosed();
            await WaitUntil(() => !simulator.State.Running);
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, state.LocalPort));
        }
        using var offline = OfflineRadioSession.Open(directory); // ownership returns to the old API
    }

    [TestMethod, TestCategory("Native")]
    public async Task SpectrumCoalescesOwnsItsStorageAndTracksRapidRetunesToNegativeOffsets()
    {
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        using var session = P2ReceiveSession.Open(NativeDirectory(), new(simulator.BasePort));
        await WaitUntil(() => session.State.AudioProduced > 48000);
        var first = session.ReadSpectrum(); Assert.IsNotNull(first);
        Assert.IsTrue(first.CoalescedFrames > 0);
        Assert.AreEqual(2, first.Ddc); Assert.AreEqual(4095, first.LevelsDb.Length);
        var copy = first.LevelsDb.ToArray();
        session.Tune(14_198_500);
        Assert.IsNull(session.ReadSpectrum()); // Tune invalidates the unread and partial FFT, not just its label
        session.Tune(14_201_000);
        Assert.IsNull(session.ReadSpectrum());
        var negative = ReceiveSelfTest.MeasureSpectrum(session, 14_201_000, 14_200_000, 3);
        Assert.IsTrue(negative.PeakOffsetHz < 0 && negative.Sequence > first.Sequence);
        CollectionAssert.AreEqual(copy, first.LevelsDb.ToArray());
        Assert.AreEqual(1, first.TuningGeneration); Assert.AreEqual(14_199_000, first.RequestedCenterFrequencyHz);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => first.FrequencyAt(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => first.FrequencyAt(first.LevelsDb.Length));
        session.Dispose();
        CollectionAssert.AreEqual(copy, first.LevelsDb.ToArray());
        AssertClosed();
    }

    [TestMethod]
    public void SpectrumFrameAxisAndAbiAreRendererIndependent()
    {
        var frame = new ReceiveSpectrumFrame(new float[4095], [1, 7, 2, 5000, 14_200_000, 9, 192000, 4096, 4095, 3, 1, 0]);
        Assert.AreEqual(14_104_000 + frame.BinWidthHz, frame.FrequencyAt(0));
        Assert.AreEqual(14_200_000, frame.FrequencyAt(2047));
        Assert.AreEqual(14_296_000 - frame.BinWidthHz, frame.FrequencyAt(4094));
        Assert.AreEqual(3, frame.CoalescedFrames); Assert.AreEqual(1, frame.MissingPackets);
        Assert.ThrowsExactly<NotSupportedException>(() => new ReceiveSpectrumFrame([], new long[12]));
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
                try { session.ReadAudio(buffer); session.ReadSpectrum(); _ = session.State; }
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
