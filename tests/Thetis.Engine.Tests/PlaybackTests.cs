using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
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
        Assert.AreEqual(PlaybackFault.SimulatedDeviceLoss,output.State.Fault);
        Assert.ThrowsExactly<IOException>(() => output.SetMuted(false));
        await Task.WhenAll(pump.DisposeAsync().AsTask(),pump.DisposeAsync().AsTask());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = output.State);
        using var reopened = PlaybackOutput.OpenNull(NativeDirectory()); Assert.IsTrue(reopened.State.Muted);
    }
    [TestMethod,TestCategory("Native"),DataRow(44100),DataRow(48000),DataRow(96000)]
    public async Task ClockedOutputDetectsStoppedCallbacksAndCannotResumeUnmuted(int rate)
    {
        using var output = PlaybackOutput.OpenClockedNull(NativeDirectory(),rate);
        Assert.IsFalse(output.State.Physical); Assert.IsTrue(output.State.ClockTracking);
        Assert.AreEqual(3072,output.State.TargetFrames);
        double[] input = new double[8192]; Array.Fill(input,.125);
        Assert.AreEqual(4096,output.Write(input,4096));
        float[] samples = new float[1920];
        output.SetMuted(false); output.RenderNull(samples,rate/100);
        Assert.IsTrue(samples[0] > .1);
        var clock = Stopwatch.StartNew();
        while (output.State.Active && clock.Elapsed.TotalSeconds < 3) await Task.Delay(25);
        Assert.IsFalse(output.State.Active); Assert.IsTrue(output.State.Muted);
        Assert.AreEqual(PlaybackFault.CallbackTimeout,output.State.Fault);
        Assert.ThrowsExactly<IOException>(() => output.SetMuted(false));
        output.RenderNull(samples,rate/100);
        Assert.IsTrue(samples.Take(2*(rate/100)).All(v => v == 0));
        Assert.ThrowsExactly<IOException>(() => output.Write(input,100));
    }
    [TestMethod,TestCategory("Native"),DataRow(1),DataRow(2)]
    public async Task ControllerReleasesFailedOutputWithoutAUiAndReconnectsMuted(int protocol)
    {
        PlaybackOutput? output = null;
        await using var controller = new PreviewController((path,_) => output = PlaybackOutput.OpenNull(path));
        for (int cycle = 0; cycle < 3; ++cycle)
        {
            await controller.ConnectAsync(NativeDirectory(),protocol);
            await P1ReceiveSelfTest.WaitUntil(() => controller.Snapshot?.Output.Rendered > 4800);
            Assert.IsNull(controller.Error); Assert.IsTrue(controller.Settings.Muted);
            Assert.IsTrue(controller.Settings.AudioGainDb <= -40);
            await controller.ApplyAsync(controller.Settings with { Muted = false,AudioGainDb = -20 });
            output!.InterruptNull();
            await P1ReceiveSelfTest.WaitUntil(() => !controller.Connected && ReceiveSession.ReadNativeState()[6] == 0);
            Assert.IsInstanceOfType<IOException>(controller.Error);
            Assert.IsTrue(controller.Settings.Muted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = output.State);
        }
        await Task.WhenAll(controller.DisconnectAsync(),controller.DisposeAsync().AsTask());
        Assert.AreEqual(0,ReceiveSession.ReadNativeState()[6]);
    }
    [TestMethod,TestCategory("Native"),TestCategory("Endurance")]
    public async Task IndependentOutputClockPlaysForSixtySecondsThroughMuteFlushAndLoss()
    {
        await using var peer = G2Simulator.Open(new(BasePort:0));
        using var rx = P2ReceiveSession.Open(NativeDirectory(),new(peer.BasePort,Gain:new(-40,false,ReceiveAgcMode.Off)));
        using var output = PlaybackOutput.OpenClockedNull(NativeDirectory());
        await using var pump = new ReceivePlayback(rx,output);
        using var stopping = new CancellationTokenSource();
        long maximumCallbackDelayUs = 0;
        Queue<string> history = new();
        // Independent +1000 ppm device clock, never paced from queue occupancy.
        // Its callback runs on a separate thread; no real audio device is opened.
        var renderer = Task.Factory.StartNew(() =>
        {
            float[] samples = new float[960]; var clock = Stopwatch.StartNew(); long blocks = 0;
            while (!stopping.IsCancellationRequested)
            {
                if (clock.Elapsed.TotalSeconds >= blocks*.01/1.001)
                {
                    long late = (long)((clock.Elapsed.TotalSeconds-blocks*.01/1.001)*1e6);
                    Volatile.Write(ref maximumCallbackDelayUs,Math.Max(maximumCallbackDelayUs,late));
                    output.RenderNull(samples,480); ++blocks;
                }
                else stopping.Token.WaitHandle.WaitOne(1);
            }
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try
        {
            var elapsed = Stopwatch.StartNew(); bool unmuted = false, flushed = false, retuned = false; double nextHistory = 0;
            while (elapsed.Elapsed.TotalSeconds < 60)
            {
                Assert.IsNull(pump.Error); var state = output.State;
                if (elapsed.Elapsed.TotalSeconds >= nextHistory || state.Underruns != 0 || state.Rejected != 0)
                {
                    history.Enqueue($"{elapsed.Elapsed.TotalSeconds:F3}s queue={state.Queued} ppm={state.CorrectionPpm:F3} submitted={state.Submitted} rendered={state.Rendered} underruns={state.Underruns} rejected={state.Rejected} produced={rx.State.AudioProduced} simulatorResyncs={peer.State.PacingResyncs} maxCallbackDelayUs={Volatile.Read(ref maximumCallbackDelayUs)}");
                    if (history.Count > 12) history.Dequeue(); nextHistory = elapsed.Elapsed.TotalSeconds+1;
                }
                Assert.IsTrue(state.Active && !state.Physical && state.ClockTracking);
                Assert.IsTrue(Math.Abs(state.CorrectionPpm) <= 2000);
                Assert.AreEqual(0,state.Rejected); Assert.AreEqual(0,state.Underruns);
                if (!unmuted && elapsed.Elapsed.TotalSeconds > 2) { pump.SetMuted(false); unmuted = true; }
                if (!flushed && elapsed.Elapsed.TotalSeconds > 20) { pump.SetMuted(true); pump.Flush(); flushed = true; }
                if (!retuned && elapsed.Elapsed.TotalSeconds > 21)
                { rx.Tune(14_198_500); pump.Flush(); pump.SetMuted(false); retuned = true; }
                await Task.Delay(25);
            }
            Assert.IsTrue(output.State.Rendered >= 60*48000);
            Assert.IsTrue(output.State.Reprimes >= 2);
            var final = pump.Snapshot!;
            foreach (long errors in new[] {final.Receive.SocketErrors,final.Receive.DspErrors,final.Receive.MissingPackets,
                final.Receive.InputOverruns,final.Receive.AudioDropped,final.Output.DriverUnderruns,final.Output.NonfiniteSamples,final.Output.ClippedSamples})
                Assert.AreEqual(0,errors);
            Console.WriteLine($"60 s independent +1000 ppm output clock: {output.State}");
            output.InterruptNull();
            await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsInstanceOfType<IOException>(pump.Error); Assert.IsTrue(output.State.Muted);
        }
        catch { Console.WriteLine(string.Join(Environment.NewLine,history)); throw; }
        finally { stopping.Cancel(); await renderer.WaitAsync(TimeSpan.FromSeconds(5)); }
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
