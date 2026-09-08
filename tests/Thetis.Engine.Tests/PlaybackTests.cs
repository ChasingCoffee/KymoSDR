using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Headless;
using Thetis.Preview;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class PlaybackTests
{
    private static string NativeDirectory()
    {
        var path = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrEmpty(path)) Assert.Inconclusive("Requires native playback and WDSP.");
        return path;
    }
    [TestMethod]
    public async Task SettingsAndCancellationRejectBeforeLoadingOrConnecting()
    {
        foreach (var invalid in new PreviewSettings[] {new(-1),new(61_440_001),new(Mode:(ReceiveMode)2),new(LowCutHz:3000),
            new(AudioGainDb:1),new(AgcMaxGainDb:81),new(AgcMode:(ReceiveAgcMode)1)})
            Assert.Throws<ArgumentException>(invalid.Validate);
        await using var controller = new PreviewController();
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ConnectAsync("unused",token:new(true)));
        Assert.IsFalse(controller.Connected);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PlaybackOutput.OpenNull("unused",22050));
        await controller.DisposeAsync();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => controller.ConnectAsync("unused"));
    }
    [TestMethod,TestCategory("Native")]
    public async Task BothProtocolsPlayMeasuredMutedAndUnmutedAudioAndReconnectSafely()
    {
        var result = await PlaybackSelfTest.RunAsync(NativeDirectory());
        Assert.IsTrue(result.Passed); Assert.IsFalse(result.PhysicalAudio); Assert.HasCount(2,result.Checks);
        Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
    }
    [TestMethod,TestCategory("Native"),DataRow(44100),DataRow(48000),DataRow(96000)]
    public void SilentMonitorPreservesLookAheadAcrossLargeBatchesAndFlush(int rate)
    {
        using var output = PlaybackOutput.OpenNull(NativeDirectory(),rate);
        output.SetMuted(false);
        double[] input = new double[4096]; Array.Fill(input,.125);
        float[] rendered = new float[1920];
        int[] batches = [2048,128,512,1920,64,480];
        for (int cycle = 0; cycle < 250; ++cycle)
        {
            int frames = batches[cycle%batches.Length]; Assert.AreEqual(frames,output.Write(input,frames));
            int blocks = 0;
            while (blocks < 5 && ReceivePlayback.RenderMonitorBlock(output,rendered,rate/100))
            {
                ++blocks;
                for (int i = 0; i < 2*(rate/100); ++i)
                    Assert.IsTrue(rendered[i] == 0 || Math.Abs(rendered[i]-.125f) < 1e-6);
            }
            // A delayed first read must not consume all 2048 input frames. The
            // old credit-only monitor eventually consumed the FIR look-ahead.
            if (cycle == 0) { Assert.AreEqual(2,blocks); Assert.IsTrue(output.QueuedSourceFrames >= 1024); }
            if (cycle%17 == 10) output.Flush();
        }
        var state = output.State;
        Assert.IsTrue(state.Rendered > rate && state.StarvationFrames > 0);
        Assert.AreEqual(0,state.Underruns); Assert.AreEqual(0,state.Rejected);
        Assert.AreEqual(0,state.ClippedSamples); Assert.AreEqual(0,state.NonfiniteSamples);
    }
    [TestMethod,TestCategory("Native")]
    public async Task OutputLossStopsPumpAndConcurrentShutdownReleasesOwners()
    {
        await using var peer = G2Simulator.Open(new(BasePort:0));
        using var rx = P2ReceiveSession.Open(NativeDirectory(),new(peer.BasePort,Gain:new(-40,true)));
        using var output = PlaybackOutput.OpenNull(NativeDirectory());
        await using var pump = new ReceivePlayback(rx,output);
        await P1ReceiveSelfTest.WaitUntil(() => pump.Snapshot?.Output.Rendered > 48000);
        output.InterruptNull();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<IOException>(pump.Error);
        Assert.IsTrue(output.State.Muted); Assert.IsFalse(output.State.Active);
        await Task.WhenAll(pump.DisposeAsync().AsTask(),pump.DisposeAsync().AsTask());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = output.State);
        using var reopened = PlaybackOutput.OpenNull(NativeDirectory()); Assert.IsTrue(reopened.State.Muted);
    }
    [TestMethod,TestCategory("Native")]
    public async Task FailedStartupInvalidControlsAndDisposeDuringConnectAreSafe()
    {
        string path = NativeDirectory(); await using var controller = new PreviewController();
        using (var held = PlaybackOutput.OpenNull(path))
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.ConnectAsync(path));
        Assert.IsFalse(controller.Connected); Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
        await controller.ConnectAsync(path);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.ApplyAsync(new(AudioGainDb:1)));
        Assert.IsTrue(controller.Connected); Assert.IsTrue(controller.Settings.Muted);
        await controller.DisconnectAsync();
        var connecting = controller.ConnectAsync(path);
        await controller.DisposeAsync();
        try { await connecting; } catch (OperationCanceledException) { }
        Assert.IsFalse(controller.Connected); Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
    }
}
