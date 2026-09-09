using Avalonia.Controls;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class HardwareUiTests
{
    private static G2RadioTarget Target => new("169.254.47.65","169.254.187.120","2c:cf:67:fc:a3:df");
    [TestMethod]
    public void HardwarePrefillCannotAutoconnectOrEnterAutomatedCampaigns()
    {
        string[] values = ["--g2-nic",Target.LocalAddress,"--g2-target",Target.RadioAddress,"--g2-mac",Target.MacAddress];
        var parsed = PreviewLaunchOptions.Parse(values); Assert.AreEqual(Target,parsed.G2Target);
        foreach (string[] bad in new string[][] { [..values,"--smoke"], [..values,"--unmute"], [..values,"--connect"],
            ["--g2-nic",Target.LocalAddress], [..values,"--g2-mac",Target.MacAddress] })
            Assert.ThrowsExactly<ArgumentException>(() => PreviewLaunchOptions.Parse(bad));
        Assert.ThrowsExactly<ArgumentException>(() => new G2HardwareRequest(Target).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => new G2HardwareRequest(Target,DurationSeconds:61,ConfirmAnt1ReceiveOnly:true).Validate());
    }
    [TestMethod]
    public async Task HardwareFormRendersAndUnconfirmedConnectCannotReachNetwork()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var controller = new PreviewController((_,_) => throw new AssertFailedException("No output permitted."),
                (_,_,_) => throw new AssertFailedException("Unconfirmed request reached hardware."));
            var window = new MainWindow(new(Path.GetTempPath(),PersistSettings:false,G2Target:Target),controller); window.Show();
            try
            {
                Assert.AreEqual(2,window.Control<ComboBox>("ProtocolInput").SelectedIndex);
                Assert.IsTrue(window.Control<StackPanel>("HardwarePanel").IsVisible);
                Assert.IsFalse(window.Control<CheckBox>("ConfirmHardwareInput").IsChecked == true);
                Assert.AreEqual("14074000",window.Control<TextBox>("FrequencyInput").Text);
                Assert.AreEqual(12000,window.Control<SpectrumView>("SpectrumDisplay").VisibleSpanHz);
                Assert.AreEqual(-150,window.Control<SpectrumView>("SpectrumDisplay").FloorDb);
                using var bitmap = window.CaptureRenderedFrame(); Assert.IsNotNull(bitmap);
                await window.Connect();
                Assert.IsFalse(controller.Connected);
                StringAssert.Contains(window.Control<TextBlock>("StatusText").Text!,"Confirm ANT1");
                window.Control<ComboBox>("ProtocolInput").SelectedIndex = 0;
                Assert.AreEqual(0,window.Control<SpectrumView>("SpectrumDisplay").VisibleSpanHz);
                Assert.IsFalse(window.Control<StackPanel>("HardwarePanel").IsVisible);
            }
            finally { await controller.DisposeAsync(); window.Close(); }
        });
    }
    [TestMethod]
    public async Task AutomatedWindowRejectsHardwareSelectionWithoutNetworking()
    {
        await UiTestHost.RunAsync(async () =>
        {
            // Do not Show: that would run the ordinary simulator smoke callback.
            var window = new MainWindow(new(Path.GetTempPath(),Smoke:true,PersistSettings:false));
            try
            {
                window.Control<ComboBox>("ProtocolInput").SelectedIndex = 2;
                Assert.AreEqual(0,window.Control<ComboBox>("ProtocolInput").SelectedIndex);
                Assert.IsFalse(window.Controller.Connected);
            }
            finally { await window.Controller.DisposeAsync(); window.Close(); }
        });
    }
    [TestMethod]
    public void HardwareDiagnosticsCannotBeMislabelledAsLoopbackOrExposeEndpoints()
    {
        var diagnostics = new PreviewDiagnostics();
        var first = diagnostics.Begin(2,new()); diagnostics.End(first,null);
        var hardware = diagnostics.Begin(2,new(FrequencyHz:14_074_000),true); diagnostics.Record(hardware,null); diagnostics.End(hardware,null);
        var report = diagnostics.Snapshot();
        Assert.IsFalse(report.LoopbackOnly); Assert.IsFalse(report.Sessions[0].Hardware); Assert.IsTrue(report.Sessions[1].Hardware);
        Assert.IsNull(report.Sessions[1].LastObserved!.SimulatorClock);
        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.IsFalse(json.Contains(Target.RadioAddress,StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(Target.MacAddress,StringComparison.Ordinal));
    }
}
