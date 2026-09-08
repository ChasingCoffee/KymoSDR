using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class ReceiveGainTests
{
    private static string NativeDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires THETIS_NATIVE_DIR.");
        return directory;
    }
    [TestMethod]
    public void InvalidSettingsCannotLoadNative()
    {
        foreach (var settings in new ReceiveGain[] {new(1),new(-61),new(AgcMode:(ReceiveAgcMode)1),new(AgcMode:(ReceiveAgcMode)5),
            new(AgcMode:(ReceiveAgcMode)(-1)),new(AgcMaxGainDb:-1),new(AgcMaxGainDb:81)})
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P1ReceiveSession.Open("unused",new(51024,Gain:settings)));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => P2ReceiveSession.Open("unused",new(51024,Gain:settings)));
        }
        Assert.ThrowsExactly<OperationCanceledException>(() => P1ReceiveSession.Open("unused",new(51024,Gain:new(-60,true,ReceiveAgcMode.Slow,80)),new(true)));
    }
    [TestMethod, TestCategory("Native"), DataRow(1), DataRow(2)]
    public async Task GainMuteAgcStepsAndReconnectHaveMeasuredSignal(int protocol)
    {
        var result = await ReceiveGainSelfTest.RunProtocolAsync(NativeDirectory(),protocol);
        Assert.AreEqual(protocol,result.Protocol); Assert.AreEqual(10,result.GainChecks.Count); Assert.AreEqual(3,result.AgcChecks.Count);
        Assert.IsTrue(result.AgcChecks.All(s => s.Peak < 1.5 && s.SilenceRms < 1e-6 && s.RecoveryWindowMilliseconds >= 0));
        Closed();
    }
    [TestMethod, TestCategory("Native")]
    public async Task PresetReadbackIsHistoryIndependentAndChangesAreValidated()
    {
        await using var simulator = G2Simulator.Open(new(BasePort:0));
        using var session = P2ReceiveSession.Open(NativeDirectory(),new(simulator.BasePort));
        var defaults = session.Gain;
        Assert.AreEqual(new ReceiveGain(),defaults.Settings); Assert.AreEqual(1,defaults.Generation);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => session.ConfigureGain(new(AgcMaxGainDb:81)));
        Assert.ThrowsExactly<ArgumentNullException>(() => session.ConfigureGain(null!));
        Assert.AreEqual(defaults,session.Gain);
        long generation = 1;
        foreach (var mode in new[] {ReceiveAgcMode.Slow,ReceiveAgcMode.Fast,ReceiveAgcMode.Slow,ReceiveAgcMode.Medium,ReceiveAgcMode.Slow,ReceiveAgcMode.Off})
        {
            var settings = new ReceiveGain(AgcMode:mode);
            session.ConfigureGain(settings); var s = session.Gain;
            Assert.AreEqual(++generation,s.Generation); Assert.AreEqual(settings,s.Settings);
            Assert.AreEqual(1,s.AttackMilliseconds);
            Assert.AreEqual(mode == ReceiveAgcMode.Slow ? 500 : mode == ReceiveAgcMode.Fast ? 50 : 250,s.DecayMilliseconds);
            Assert.AreEqual(mode == ReceiveAgcMode.Slow ? 1000 : 0,s.HangMilliseconds);
            Assert.AreEqual(mode == ReceiveAgcMode.Slow ? 25 : 100,s.HangThresholdPercent);
            session.ConfigureGain(settings); Assert.AreEqual(s,session.Gain);
        }
        Assert.AreEqual(new ReceiveDemodulationState(new(),1),session.Demodulation);
        session.Dispose(); Assert.ThrowsExactly<ObjectDisposedException>(() => session.ConfigureGain(new()));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = session.Gain); Closed();
    }
    [TestMethod, TestCategory("Native")]
    public async Task ConfiguredStartupRollsBackAtEveryStageAndConcurrentDisposeIsSafe()
    {
        string directory = NativeDirectory(); await using var simulator = P1Simulator.Open();
        var options = new P1ReceiveOptions(simulator.Port,Gain:new(-20,true,ReceiveAgcMode.Slow,40));
        for (int target = 1; target <= 5; ++target)
        {
            int at = target; using var cancellation = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => P1ReceiveSession.OpenCore(directory,options,cancellation.Token,stage =>
            { if (stage == at) { cancellation.Cancel(); return 1; } return 0; }));
            Closed(); await P1ReceiveSelfTest.WaitUntil(() => !simulator.State.Running);
        }
        using var session = P1ReceiveSession.Open(directory,options);
        Assert.AreEqual(options.Gain,session.Gain.Settings); Assert.AreEqual(1,session.Gain.Generation);
        Assert.ThrowsExactly<OperationCanceledException>(() => ReceiveControlsSelfTest.MeasureSettled(session,new(true)));
        var reader = Task.Run(() =>
        {
            double[] audio = new double[4096];
            for (int i = 0; i < 200; ++i)
            { try { session.ReadAudio(audio); _ = session.Gain; } catch (ObjectDisposedException) { return; } }
        });
        var controls = Task.Run(() =>
        {
            for (int i = 0; i < 100; ++i)
            { try { session.ConfigureGain(new(-i%60,i%2 == 0,ReceiveAgcMode.Fast)); } catch (ObjectDisposedException) { return; } }
        });
        await Task.WhenAll(reader,controls,Task.Run(session.Dispose)).WaitAsync(TimeSpan.FromSeconds(10)); Closed();
    }
    private static void Closed()
    { Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]); Assert.AreEqual(0,OfflineRadioSession.ReadNativeState()[10]); }
}
