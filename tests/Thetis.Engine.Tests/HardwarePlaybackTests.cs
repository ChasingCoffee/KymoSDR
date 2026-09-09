using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Engine.Tests;

[TestClass,DoNotParallelize]
public sealed class HardwarePlaybackTests
{
    [TestMethod]
    public void CaptureIsBoundedFrozenExclusiveAndDoesNotModifySourceSamples()
    {
        var capture = new ReceiveAudioCapture(1,DateTimeOffset.MinValue);
        double[] source = [0.0001,0.0001,-0.0002,-0.0002];
        string path = Path.Combine(Path.GetTempPath(),$"g2-capture-{Guid.NewGuid():N}.wav");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => capture.SaveWave(path));
            for (int i = 0; i < 30000; ++i) capture.Add(source,2);
            Assert.AreEqual(48000,capture.Frames); Assert.AreEqual(0.0001,source[0]);
            capture.Freeze(); capture.SaveWave(path);
            var bytes = File.ReadAllBytes(path); Assert.AreEqual(96044,bytes.Length);
            Assert.AreEqual(48000,BitConverter.ToInt32(bytes,24)); Assert.AreEqual(96000,BitConverter.ToInt32(bytes,40));
            Assert.AreEqual(8192,BitConverter.ToInt16(bytes,44)); Assert.AreEqual(-16383,BitConverter.ToInt16(bytes,46));
            Assert.ThrowsExactly<IOException>(() => capture.SaveWave(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [TestMethod,TestCategory("Native"),DataRow(1),DataRow(4),DataRow(3)]
    public async Task PlaybackStopsAndMutesWhenHardwareDeadlineOrSafetyTripIsObserved(int reason)
    {
        string? directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires native libraries; test uses only loopback/no-device.");
        await using var simulator = G2Simulator.Open(new(BasePort:0));
        using var receiver = P2ReceiveSession.Open(directory,new(simulator.BasePort));
        using var output = PlaybackOutput.OpenNull(directory);
        int finished = 0;
        await using var pump = new ReceivePlayback(receiver,output,() => new(Volatile.Read(ref finished) == 0,
            Volatile.Read(ref finished) == 0 ? 0 : reason,reason == 4 ? 1 : 0,0,5,0));
        await Task.Delay(300); pump.SetMuted(false); Volatile.Write(ref finished,1);
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(reason,pump.Snapshot!.Hardware!.StopReason); Assert.IsTrue(pump.Snapshot.Output.Muted);
        if (reason == 1) Assert.IsNull(pump.Error); else Assert.IsInstanceOfType<IOException>(pump.Error);
    }
}
