using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Preview;

namespace Thetis.Engine.Tests;

[TestClass,DoNotParallelize]
public sealed class AudioControlsTests
{
    [TestMethod]
    public void ExplicitPairsAndListeningPresetPreserveSafetyAndTuning()
    {
        PlaybackPair[] pairs = [new(0,"Main L","Main R",7),new(10,"Phones L","Phones R",1)];
        var device = new PlaybackDevice(0,"Fixture","Fixture",48000,7,32,pairs);
        var phones = device.WithPair(pairs[1]);
        Assert.AreEqual(10,phones.FirstOutputChannel); Assert.AreEqual(44100,phones.PreferredRate);
        Assert.AreEqual(0,device.FirstOutputChannel);
        Assert.ThrowsExactly<ArgumentException>(() => device.WithPair(new(2,"Other","Other",7)));
        Assert.ThrowsExactly<ArgumentException>(() => PlaybackOutput.OpenDevice("unused",phones with { OutputChannels = 2 }));
        var draft = new PreviewSettings(FrequencyHz:14_074_000,LowCutHz:100,AudioGainDb:-40);
        var listening = draft.ForListening(true);
        Assert.AreEqual(draft.FrequencyHz,listening.FrequencyHz); Assert.AreEqual(draft.Mode,listening.Mode);
        Assert.AreEqual(draft.LowCutHz,listening.LowCutHz); Assert.AreEqual(-10,listening.AudioGainDb);
        Assert.AreEqual(80,listening.AgcMaxGainDb); Assert.IsFalse(listening.Muted); Assert.IsTrue(draft.Muted);
        Assert.AreEqual(-40,draft.ForListening(false).AudioGainDb);
        Assert.AreEqual(-20,PlaybackLevels.Decibels(.1),.0001);
        Assert.AreEqual(-96,PlaybackLevels.Decibels(0)); Assert.AreEqual(-96,PlaybackLevels.Decibels(double.NaN));
    }

    [TestMethod,TestCategory("Native"),DataRow(44100),DataRow(48000),DataRow(96000)]
    public void StereoMetersTrackRecentOutputNotSessionPeakAndClearOnMute(int rate)
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Native library required; no-device output only.");
        using var output = PlaybackOutput.OpenNull(directory,rate); output.SetMuted(false);
        double[] input = new double[960]; float[] rendered = new float[1920];
        void Feed(double left,double right)
        {
            for (int i = 0; i < 480; ++i) { input[2*i] = left; input[2*i+1] = right; }
            for (int i = 0; i < 40; ++i)
            {
                Assert.AreEqual(480,output.Write(input,480));
                while (ReceivePlayback.RenderMonitorBlock(output,rendered,rate/100)) { }
            }
        }
        Feed(.2,.05);
        var first = output.State.Levels!;
        Assert.AreEqual(.2,first.LeftPeak,.000001); Assert.AreEqual(.05,first.RightRms,.000001);
        Feed(.002,.001);
        var next = output.State;
        Assert.IsTrue(next.Levels!.Sequence > first.Sequence);
        Assert.AreEqual(.002,next.Levels.LeftPeak,.000001); Assert.AreEqual(.001,next.Levels.RightRms,.000001);
        // The abrupt level step can ring in the FIR resampler. The lifetime
        // maximum retains that transient; the current-window meter must not.
        Assert.IsTrue(next.Peak >= .19999 && next.Peak < .25);
        output.SetMuted(true); Assert.AreEqual(PlaybackLevels.Silence,output.State.Levels);
        Feed(0,0); output.SetMuted(false); Feed(0,0);
        Assert.AreEqual(0,output.State.Levels!.LeftPeak); Assert.AreEqual(0,output.State.Levels.RightRms);
        Assert.AreEqual(0,output.State.Underruns); Assert.AreEqual(0,output.State.Rejected);
    }
}
