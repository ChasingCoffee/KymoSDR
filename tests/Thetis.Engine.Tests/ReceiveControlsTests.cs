using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class ReceiveControlsTests
{
    private static string NativeDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return directory;
    }

    [TestMethod]
    public void InvalidControlsFailBeforeNativeLoadAndSidebandMappingIsExplicit()
    {
        foreach (var settings in new ReceiveDemodulation[] { new((ReceiveMode)2), new(LowCutHz: -1), new(HighCutHz: 12001),
            new(LowCutHz: 3000), new(HighCutHz: 399), new(LowCutHz: int.MaxValue), new(HighCutHz: int.MinValue) })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P2ReceiveSession.Open("unused", new(51024, Demodulation: settings)));
        foreach (var mode in new[] { ReceiveMode.Usb, ReceiveMode.Lsb })
        {
            var settings = new ReceiveDemodulation(mode, 0, 12000); settings.Validate();
            Assert.AreEqual(mode == ReceiveMode.Usb ? 0 : -12000, settings.SignedLowHz);
            Assert.AreEqual(mode == ReceiveMode.Usb ? 12000 : 0, settings.SignedHighHz);
            new ReceiveDemodulation(mode, 11900, 12000).Validate();
            Assert.ThrowsExactly<OperationCanceledException>(() => P2ReceiveSession.Open("unused", new(51024, Demodulation: settings), new(true)));
        }
    }

    [TestMethod, TestCategory("Native")]
    public async Task BothSidebandsFiltersAndReconnectPassSignalChecks()
    {
        var result = await ReceiveControlsSelfTest.RunAsync(NativeDirectory());
        Assert.IsTrue(result.Passed && result.LoopbackOnly && !result.TransmitAllowed);
        Assert.AreEqual(12, result.Checks.Count);
        Assert.IsTrue(result.Checks.All(c => c.Passed));
        Assert.AreEqual(6, result.Checks.Count(c => c.ExpectedPassband));
        Assert.AreEqual(0, result.Simulator.Transmit.Packets);
    }

    [TestMethod, TestCategory("Native")]
    public async Task UpdatesAreValidatedVersionedAndSafeWithConcurrentPullAndDispose()
    {
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        using var session = P2ReceiveSession.Open(NativeDirectory(), new(simulator.BasePort));
        var original = session.Demodulation;
        Assert.AreEqual(new ReceiveDemodulationState(new(), 1), original);
        Assert.ThrowsExactly<ArgumentNullException>(() => session.ConfigureDemodulation(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.ConfigureDemodulation(new(HighCutHz: 200)));
        Assert.AreEqual(original, session.Demodulation);
        session.ConfigureDemodulation(new()); Assert.AreEqual(original, session.Demodulation);
        var next = new ReceiveDemodulation(ReceiveMode.Lsb, 500, 2500);
        await Task.Run(() => session.ConfigureDemodulation(next));
        Assert.AreEqual(new ReceiveDemodulationState(next, 2), session.Demodulation);
        var reader = Task.Run(() =>
        {
            double[] audio = new double[4096];
            for (int i = 0; i < 200; ++i)
            {
                try { session.ReadAudio(audio); session.ReadSpectrum(); _ = session.Demodulation; }
                catch (ObjectDisposedException) { return; }
            }
        });
        var updates = Task.Run(() =>
        {
            for (int i = 0; i < 20; ++i)
            {
                try { session.ConfigureDemodulation(new(i % 2 == 0 ? ReceiveMode.Usb : ReceiveMode.Lsb)); }
                catch (ObjectDisposedException) { return; }
            }
        });
        var dispose = Task.Run(session.Dispose);
        await Task.WhenAll(reader, updates, dispose).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.ConfigureDemodulation(new()));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.Demodulation);
        Assert.AreEqual(0, P2ReceiveSession.ReadNativeState()[6]);
        Assert.AreEqual(0, OfflineRadioSession.ReadNativeState()[10]);
    }

    [TestMethod, TestCategory("Native")]
    public async Task FailedStartupAndCancelledMeasurementsReleaseConfiguredReceiver()
    {
        string directory = NativeDirectory();
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        var options = new P2ReceiveOptions(simulator.BasePort, FrequencyHz: 14_201_000, Demodulation: new(ReceiveMode.Lsb, 500, 2500));
        using var cancel = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() => P2ReceiveSession.OpenCore(directory, options, cancel.Token, stage =>
        { if (stage == 5) { cancel.Cancel(); return 1; } return 0; }));
        using (var session = P2ReceiveSession.Open(directory, options))
        {
            Assert.AreEqual(new ReceiveDemodulationState(options.Demodulation!, 1), session.Demodulation);
            Assert.ThrowsExactly<OperationCanceledException>(() => ReceiveControlsSelfTest.MeasureSettled(session, new(true)));
        }
        using var reopened = P2ReceiveSession.Open(directory, new(simulator.BasePort));
        Assert.AreEqual(new ReceiveDemodulationState(new(), 1), reopened.Demodulation);
    }
}
