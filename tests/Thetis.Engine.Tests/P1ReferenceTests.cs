using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class P1ReferenceTests
{
    [TestMethod, TestCategory("Native")]
    public async Task PinnedHpsdrsimStreamsIndependentToneAndAcceptsRetuneAndStop()
    {
        string? fixture = Environment.GetEnvironmentVariable("THETIS_P1_REFERENCE_SIM");
        string? native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(fixture) || string.IsNullOrWhiteSpace(native))
            Assert.Inconclusive("Requires THETIS_P1_REFERENCE_SIM (confined build) and THETIS_NATIVE_DIR.");
        Assert.IsTrue(Path.IsPathFullyQualified(fixture) && File.Exists(fixture));
        using var process = new Process { StartInfo = new(fixture) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tuned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lines) { if (lines.Count < 200) lines.Add(e.Data); }
            if (e.Data.StartsWith("THETIS_P1_READY ",StringComparison.Ordinal))
                ready.TrySetResult(int.Parse(e.Data.AsSpan(16),CultureInfo.InvariantCulture));
            if (e.Data.Contains("RX FREQ1",StringComparison.Ordinal) && e.Data.Contains("14198500",StringComparison.Ordinal)) tuned.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (lines) { if (lines.Count < 200) lines.Add(e.Data); }
            if (e.Data.Contains("STOP the transmission via handler_ep6",StringComparison.Ordinal)) stop.TrySetResult();
        };
        Assert.IsTrue(process.Start()); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            int port = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var session = P1ReceiveSession.Open(native,new(port,Demodulation:new(ReceiveMode.Lsb)));
            var audio = ReceiveControlsSelfTest.MeasureSettled(session);
            Assert.AreEqual(800,audio.ToneHz,20); Assert.IsTrue(audio.Rms is > .0001 and < .0003,$"RMS={audio.Rms:G6}");
            var frame = session.ReadSpectrum(); Assert.IsNotNull(frame);
            // Upstream uses I=sin,Q=cos: fixed -800 and -4000 Hz baseband tones,
            // independent of its logged RX frequency. It cannot validate RF retuning.
            var levels = frame.LevelsDb.ToArray();
            foreach (int offset in new[] {-800,-4000})
            {
                int bin = (int)Math.Round((offset+24000)/frame.BinWidthHz)-1;
                Assert.IsTrue(float.IsFinite(levels[bin]) && levels[bin] is > -77 and < -68,$"{offset} Hz level {levels[bin]}");
            }
            session.Tune(14_198_500); await tuned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var state = session.State; P1ReceiveSelfTest.RequireClean(state);
            session.Dispose(); await stop.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback,state.LocalPort));
            Console.WriteLine($"Independent hpsdrsim: {audio.ToneHz:F3} Hz, RMS {audio.Rms:G9}, {state.IqPackets} packets; -800/-4000 Hz spectrum, retune command, STOP and rebind passed.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            lock (lines) foreach (string line in lines) Console.WriteLine(line);
        }
    }
}
