using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class ReceiveSoakTests
{
    [TestMethod]
    public void OptionsAreBoundedAndCannotSelectHardwareOrTransmit()
    {
        string path = Path.GetTempPath();
        var parsed = ReceiveSoakCli.Parse(["receive-soak", "--native-dir", path]);
        Assert.AreEqual(path, parsed.Directory); Assert.AreEqual(new ReceiveSoakOptions(), parsed.Options);
        parsed = ReceiveSoakCli.Parse(["receive-soak", "--reconnects", "10", "--native-dir", path, "--duration-seconds", "1800"]);
        Assert.AreEqual(new ReceiveSoakOptions(1800, 10), parsed.Options);
        foreach (string[] extra in new string[][]
        {
            ["--duration-seconds", "9"], ["--duration-seconds", "7201"], ["--duration-seconds", "NaN"],
            ["--duration-seconds", "10.5"], ["--duration-seconds", "999999999999999"],
            ["--reconnects", "0"], ["--reconnects", "21"], ["--reconnects"],
            ["--radio", "127.0.0.1"], ["--tx"], ["--native-dir", path],
            ["--duration-seconds", "10", "--duration-seconds", "20"]
        }) Assert.Throws<ArgumentException>(() => ReceiveSoakCli.Parse(["receive-soak", "--native-dir", path, ..extra]));
        Assert.Throws<ArgumentException>(() => ReceiveSoakCli.Parse(["receive-soak"]));
        Assert.Throws<ArgumentException>(() => ReceiveSoakCli.Parse(["receive-soak", "--native-dir", "relative"]));
    }

    [TestMethod]
    public async Task HelpInvalidOptionsAndPreCancellationNeverInvokeRunner()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, await ReceiveSoakCli.RunAsync(["receive-soak", "--help"], output, TextWriter.Null,
            runner: (_, _, _, _) => throw new AssertFailedException()));
        StringAssert.Contains(output.ToString(), "No hardware, TX");
        Assert.AreEqual(2, await ReceiveSoakCli.RunAsync(["receive-soak", "--radio", "192.0.2.1"], TextWriter.Null, TextWriter.Null,
            runner: (_, _, _, _) => throw new AssertFailedException()));
        Assert.AreEqual(130, await ReceiveSoakCli.RunAsync(["receive-soak", "--native-dir", Path.GetTempPath()], TextWriter.Null,
            TextWriter.Null, new(true), runner: (_, _, _, _) => throw new AssertFailedException()));
    }

    [TestMethod]
    public async Task JsonResultAndProgressStaySeparateAndFailureExitsAreDistinct()
    {
        string[] args = ["receive-soak", "--native-dir", Path.GetTempPath()];
        foreach (var (passed, cancelled, exit) in new[] { (true, false, 0), (false, false, 4), (false, true, 130) })
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.AreEqual(exit, await ReceiveSoakCli.RunAsync(args, output, error, runner: (_, _, progress, _) =>
            {
                progress(new("steady", 10, 8000, 480000, 200, 1000000));
                return Task.FromResult(new ReceiveSoakResult(1, passed, cancelled, passed ? null : "partial", true, false, 10, 1, 10000, []));
            }));
            using var json = JsonDocument.Parse(output.ToString());
            Assert.AreEqual(passed, json.RootElement.GetProperty("passed").GetBoolean());
            Assert.AreEqual(cancelled, json.RootElement.GetProperty("cancelled").GetBoolean());
            StringAssert.Contains(error.ToString(), "[steady]");
        }
        Assert.AreEqual(3, await ReceiveSoakCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _, _, _) => throw new EntryPointNotFoundException("old native library")));
        Assert.AreEqual(4, await ReceiveSoakCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _, _, _) => throw new IOException("fixture failed")));
    }

    [TestMethod]
    public void FaultAllowanceDoesNotMaskUnrelatedErrors()
    {
        ReceiveState clean = new(30000, 20000, 2, 192000, 1, 1000, 238000, 0, 0, 0, 0, 100, 1, 0, 10, 0, 0, 0, 59000, 0);
        ReceiveSoak.ValidateState(clean, false, false);
        ReceiveSoak.ValidateState(clean with { MissingPackets = 100 }, true, false);
        ReceiveSoak.ValidateState(clean with { AudioDropped = 32000, AudioQueued = 16384 }, false, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => ReceiveSoak.ValidateState(clean with { MissingPackets = 1 }, false, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ReceiveSoak.ValidateState(clean with { AudioDropped = 1 }, true, false));
        foreach (var invalid in new[] { clean with { InputOverruns = 1 }, clean with { DspErrors = 1 },
            clean with { SocketErrors = 1 }, clean with { ForeignPackets = 1 }, clean with { MalformedPackets = 1 },
            clean with { LatePackets = 1 }, clean with { AudioQueued = 16385 }, clean with { AudioQueued = -1 } })
            Assert.ThrowsExactly<InvalidOperationException>(() => ReceiveSoak.ValidateState(invalid, true, true));
    }

    [TestMethod]
    public void AudioSignalChecksDetectSilenceWrongFrequencyAndGain()
    {
        static double[] Tone(double frequency, double amplitude) => Enumerable.Range(0, 8192 * 2)
            .Select(i => amplitude * Math.Sin(2 * Math.PI * frequency * (i / 2) / 48000)).ToArray();
        var signal = new ReceiveSoakSignal();
        Assert.AreEqual(1, signal.Add(Tone(1000, 0.25), 1000));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ReceiveSoakSignal().Add(Tone(1500, 0.25), 1000));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ReceiveSoakSignal().Add(Tone(1000, 0.1), 1000));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ReceiveSoakSignal().Add(new double[16384], 1000));
    }
}
