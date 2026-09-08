using System.Text.Json;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class DiagnosticsTests
{
    [TestMethod]
    public void MetricsHaveExplicitUnitsAndRetainedBoundedHistory()
    {
        double seconds = 0; long memory = 1000;
        var diagnostics = new PreviewDiagnostics(() => seconds,() => new(memory,500,(long)seconds*100,seconds*.5));
        long id = diagnostics.Begin(2,new());
        for (int i = 0; i < 4000; ++i)
        {
            seconds = i; memory = 1000+i;
            diagnostics.PublishDisplay(new(30*i,25*i,5*i,1000,600)); diagnostics.Record(id,null);
        }
        var report = diagnostics.Snapshot(); var sample = report.Samples[^1];
        Assert.AreEqual(3600,report.Samples.Length); Assert.AreEqual(400L,report.EvictedSamples);
        Assert.AreEqual(4000L,report.Resources.SamplesRecorded); Assert.AreEqual(4999L,report.Resources.PeakWorkingSetBytes);
        Assert.AreEqual(50,sample.CpuPercentOneCore); Assert.AreEqual(30,sample.DisplayFramesPerSecond); Assert.AreEqual(25,sample.RenderFramesPerSecond);
        diagnostics.End(id,null); seconds += 10;
        diagnostics.Record(id,null); Assert.AreEqual(4000L,diagnostics.Snapshot().Resources.SamplesRecorded);
        long next = diagnostics.Begin(1,new()); diagnostics.Record(id,null); diagnostics.Record(next,null);
        Assert.AreEqual(10,diagnostics.Snapshot().Samples[^1].SampleGapSeconds);
        diagnostics.End(next,new IOException("/sensitive/local/path: not exported"));
        for (int i = 0; i < 40; ++i) { long connection = diagnostics.Begin(2,new()); diagnostics.End(connection,null); }
        for (int i = 0; i < 300; ++i) diagnostics.Event("test-event");
        report = diagnostics.Snapshot(); Assert.AreEqual(32,report.Sessions.Length); Assert.AreEqual(10L,report.EvictedSessions);
        Assert.AreEqual(256,report.Events.Length); Assert.IsTrue(report.EvictedEvents > 0); Assert.AreEqual(1L,report.SessionsFailed);
        string json = JsonSerializer.Serialize(report,AtomicJsonFile.Options);
        Assert.IsFalse(json.Contains("sensitive",StringComparison.Ordinal)); Assert.IsFalse(json.Contains("levelsDb",StringComparison.Ordinal));
        Assert.IsTrue(report.LoopbackOnly);
    }
    [TestMethod]
    public async Task SnapshotExportIsImmutableAndCanBeReadAfterTheSessionEnds()
    {
        double seconds = 0;
        var diagnostics = new PreviewDiagnostics(() => seconds,() => new(1000,500,100,0));
        long id = diagnostics.Begin(1,new()); diagnostics.Record(id,null);
        var snapshot = diagnostics.Snapshot(); seconds = 2; diagnostics.End(id,new IOException("private error details"));
        Assert.IsNull(snapshot.Sessions[0].EndedSeconds);
        string directory = Path.Combine(Path.GetTempPath(),$"kymosdr-report-{Guid.NewGuid():N}");
        string path = Path.Combine(directory,"session.json");
        try
        {
            await diagnostics.ExportAsync(path);
            var saved = JsonSerializer.Deserialize<DiagnosticReport>(File.ReadAllBytes(path),AtomicJsonFile.Options)!;
            Assert.AreEqual(1,saved.SchemaVersion); Assert.AreEqual(2,saved.Sessions[0].EndedSeconds);
            Assert.AreEqual("IOException",saved.Sessions[0].ErrorType); Assert.AreEqual(1L,saved.SessionsFailed);
            Assert.IsFalse(File.ReadAllText(path).Contains("private error details",StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,recursive:true); }
    }
    [TestMethod]
    public void AutomatedArgumentsAreBoundedAndDisableSettings()
    {
        foreach (string[] args in new string[][] { ["--endurance-seconds","29"], ["--endurance-seconds","3601"],
            ["--endurance-seconds","30"], ["--report",Path.GetTempPath()], ["--smoke","--endurance-seconds","30","--report",Path.GetTempPath()] })
            Assert.ThrowsExactly<ArgumentException>(() => PreviewLaunchOptions.Parse(args));
        var options = PreviewLaunchOptions.Parse(["--endurance-seconds","60","--report",Path.Combine(Path.GetTempPath(),"report.json")]);
        Assert.IsTrue(options.Automated); Assert.IsFalse(options.PersistSettings); Assert.AreEqual(60,options.EnduranceSeconds);
        Assert.IsFalse(PreviewLaunchOptions.Parse(["--smoke"]).PersistSettings);
        Assert.IsFalse(PreviewLaunchOptions.Parse(["--no-settings"]).PersistSettings);
    }
    [TestMethod,TestCategory("Native"),TestCategory("DesktopEndurance")]
    public async Task DesktopEnduranceUsesRealControlsAndBoundedDiagnosticsWithoutDevices()
    {
        var native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(native)) Assert.Inconclusive("Requires native libraries; only owned loopback and no-device output.");
        int seconds = 30;
        string? requested = Environment.GetEnvironmentVariable("THETIS_DESKTOP_ENDURANCE_SECONDS");
        if (requested is not null && (!int.TryParse(requested,out seconds) || seconds is < 30 or > 3600)) Assert.Fail("Endurance override must be 30..3600 seconds.");
        await UiTestHost.RunAsync(async () =>
        {
            // Do not set EnduranceSeconds: the test invokes the shared campaign,
            // not the native application's Opened-event auto-shutdown handler.
            var window = new MainWindow(new(native,PersistSettings:false)); window.Show();
            try
            {
                var report = await window.RunEnduranceCampaign(seconds,() =>
                { using var frame = window.CaptureRenderedFrame(); Assert.IsNotNull(frame); });
                string? path = Environment.GetEnvironmentVariable("THETIS_DESKTOP_ENDURANCE_REPORT");
                if (path is not null) await AtomicJsonFile.WriteAsync(path,report);
                Console.WriteLine(JsonSerializer.Serialize(new { report.Passed,report.ElapsedSeconds,report.FailureType,report.Phases,report.Diagnostics.Resources }));
                Assert.IsTrue(report.Passed,JsonSerializer.Serialize(report.Diagnostics.Events,AtomicJsonFile.Options));
                Assert.AreEqual(3,report.Phases.Length); Assert.AreEqual(3L,report.Diagnostics.SessionsStarted);
                Assert.IsFalse(window.Controller.Connected);
                Assert.IsTrue(report.Diagnostics.Resources.SamplesRecorded >= seconds-3);
                Assert.IsTrue(report.Diagnostics.Resources.PeakWorkingSetBytes > 0);
                Assert.IsTrue(report.Diagnostics.Sessions.All(s => s.EndedSeconds is not null && s.LastObserved?.Output?.Physical == false));
                Assert.IsTrue(report.Phases.All(p => p.ConnectedSeconds >= seconds/3.0 && p.RenderedFrames >= 10));
            }
            finally
            {
                await window.Controller.DisposeAsync(); window.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (window.IsVisible) await Task.Delay(25,timeout.Token);
            }
        });
    }
}
