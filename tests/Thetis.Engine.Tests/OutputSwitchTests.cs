using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Headless;
using Thetis.Preview;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass,DoNotParallelize]
public sealed class OutputSwitchTests
{
    private static string Native()
    {
        var path = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Native libraries required; only simulator/no-device output is used.");
        return path;
    }
    private static PlaybackDevice Device(int index,int rates = 7) => new(index,$"Fixture {index}","Fixture",48000,rates,32,
        [new(0,"Main L","Main R",rates),new(10,"Phones L","Phones R",rates)],10);

    [TestMethod,TestCategory("Native"),DataRow(1),DataRow(2)]
    public async Task SwitchesRatesPairsAndSilentMonitorWithoutRestartingTheReceiver(int protocol)
    {
        int opens = 0;
        await using var controller = new PreviewController((path,device) =>
        { ++opens; return PlaybackOutput.OpenNull(path,device?.PreferredRate ?? 48000); });
        await controller.ConnectAsync(Native(),protocol);
        await P1ReceiveSelfTest.WaitUntil(() => controller.Snapshot?.Output.Rendered > 4800);
        var initial = controller.Snapshot!.Receive;
        await controller.ApplyAsync(controller.Settings with { AudioGainDb = -20,Muted = false,AgcMode = ReceiveAgcMode.Off });
        var settings = controller.Settings; long generation = 1;
        foreach (var device in new PlaybackDevice?[] {Device(1,1),Device(2,4),null,Device(3),null})
        {
            await controller.SwitchOutputAsync(device); ++generation;
            await P1ReceiveSelfTest.WaitUntil(() => controller.Snapshot is { } s && s.Output.Generation == generation && s.Output.Rendered > 4800);
            var snapshot = controller.Snapshot!;
            Assert.IsTrue(controller.Connected); Assert.AreEqual(device,controller.CurrentOutput);
            Assert.AreEqual(settings,controller.Settings); Assert.IsFalse(snapshot.Output.Muted);
            Assert.AreEqual(device?.PreferredRate ?? 48000,snapshot.Output.Rate);
            Assert.AreEqual(initial.LocalPort,snapshot.Receive.LocalPort); Assert.AreEqual(initial.BasePort,snapshot.Receive.BasePort);
            Assert.IsTrue(snapshot.Receive.IqPackets > initial.IqPackets); Assert.AreEqual(1L,controller.Diagnostics.Snapshot().SessionsStarted);
            Assert.AreEqual(0,snapshot.Receive.AudioDropped); Assert.AreEqual(0,snapshot.Receive.InputOverruns);
            Assert.AreEqual(0,snapshot.Output.Rejected); Assert.IsFalse(snapshot.Output.Switching);
        }
        Assert.AreEqual(6,opens);
        await controller.ApplyAsync(controller.Settings with { Muted = true });
        await controller.SwitchOutputAsync(Device(4)); Assert.IsTrue(controller.Settings.Muted);
        await controller.DisconnectAsync(); Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
    }
    [TestMethod,TestCategory("Native")]
    public async Task PumpDrainsAndPublishesDuringSlowOpenWithoutAnAudioBacklog()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int opens = 0;
        await using var controller = new PreviewController((path,_) =>
        {
            if (++opens > 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return PlaybackOutput.OpenNull(path);
        });
        await controller.ConnectAsync(Native());
        await P1ReceiveSelfTest.WaitUntil(() => controller.Snapshot?.Receive.IqPackets > 100);
        long before = controller.Snapshot!.Receive.IqPackets;
        var change = controller.SwitchOutputAsync(Device(1));
        try
        {
            await P1ReceiveSelfTest.WaitUntil(() => entered.IsSet && controller.Snapshot is { Output.Switching:true } s &&
                s.Receive.IqPackets > before+100 && s.Output.SwitchDiscardedFrames > 4800);
            Assert.IsTrue(controller.Connected); Assert.IsFalse(change.IsCompleted);
            Assert.AreEqual(0,controller.Snapshot!.Receive.AudioDropped); Assert.AreEqual(0,controller.Snapshot.Receive.InputOverruns);
            Assert.AreEqual(0,controller.Snapshot.Output.Levels!.LeftPeak);
        }
        finally { release.Set(); await change; }
        await P1ReceiveSelfTest.WaitUntil(() => controller.Snapshot is { Output.Generation:2,Output.Switching:false } s && s.Output.Rendered > 4800);
        Assert.AreEqual(0,controller.Snapshot!.Output.Rejected); Assert.IsNull(controller.Error);
    }
    [TestMethod,TestCategory("Native")]
    public async Task FailedOutputOpenClosesTheSourceWithoutTryingAnotherDevice()
    {
        int opens = 0;
        await using var controller = new PreviewController((path,_) =>
        { if (++opens > 1) throw new IOException("Fixture open failure."); return PlaybackOutput.OpenNull(path); });
        await controller.ConnectAsync(Native());
        await Assert.ThrowsExactlyAsync<IOException>(() => controller.SwitchOutputAsync(Device(1)));
        Assert.AreEqual(2,opens); Assert.IsFalse(controller.Connected); Assert.IsTrue(controller.Settings.Muted);
        Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
        using var noLeakedOutput = PlaybackOutput.OpenNull(Native());
    }
    [TestMethod,TestCategory("Native")]
    public async Task DisconnectDuringOutputOpenPreventsLateAttachAndJoinsBothOwners()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); int opens = 0;
        await using var controller = new PreviewController((path,_) =>
        {
            if (++opens > 1) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
            return PlaybackOutput.OpenNull(path);
        });
        await controller.ConnectAsync(Native()); var change = controller.SwitchOutputAsync(Device(1));
        await P1ReceiveSelfTest.WaitUntil(() => entered.IsSet);
        var disconnect = controller.DisconnectAsync(); release.Set();
        await Assert.ThrowsAsync<OperationCanceledException>(() => change);
        await disconnect.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(controller.Connected); Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
        using var noLeakedOutput = PlaybackOutput.OpenNull(Native());
    }
    [TestMethod,TestCategory("Native")]
    public async Task ConnectedRefreshRevalidatesTheActiveRouteWithoutFollowingTheSystemDefault()
    {
        await using var controller = new PreviewController((path,device) => PlaybackOutput.OpenNull(path,device?.PreferredRate ?? 48000));
        await controller.ConnectAsync(Native(),device:Device(1));
        var reordered = Device(1) with { Index = 81 };
        var result = await controller.RefreshOutputsAsync(_ => [Device(2) with { IsSystemDefault = true },reordered]);
        Assert.AreEqual(2,result.Count); Assert.AreEqual(reordered,controller.CurrentOutput); Assert.IsTrue(controller.Connected);
        await Assert.ThrowsExactlyAsync<IOException>(() => controller.RefreshOutputsAsync(_ => [Device(2)]));
        Assert.IsFalse(controller.Connected); Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
    }
    [TestMethod,TestCategory("Native")]
    public async Task HardwareDeadlineDuringHandoffCannotRestartReceiveOrLeaveAnOutputOpen()
    {
        await using var simulator = G2Simulator.Open(new(BasePort:0));
        using var rx = P2ReceiveSession.Open(Native(),new(simulator.BasePort));
        int running = 1;
        await using var pump = new ReceivePlayback(rx,PlaybackOutput.OpenNull(Native()),
            () => new(Volatile.Read(ref running) == 1,Volatile.Read(ref running) == 1 ? 0 : 1,0,0,0,0));
        await P1ReceiveSelfTest.WaitUntil(() => pump.Snapshot?.Output.Rendered > 4800);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pump.ReplaceOutputAsync(() =>
        {
            Volatile.Write(ref running,0);
            if (!pump.Completion.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return PlaybackOutput.OpenNull(Native());
        },false));
        await pump.DisposeAsync(); using var noLeakedOutput = PlaybackOutput.OpenNull(Native());
    }
}
