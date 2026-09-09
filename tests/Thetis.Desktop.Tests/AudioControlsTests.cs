using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;
using Thetis.Simulator;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class AudioControlsTests
{
    private static PlaybackDevice Device => new(9,"Fixture output","Fixture",48000,7,32,
        [new(0,"Main Out L","Main Out R",7),new(10,"Phones 1L","Phones 1R",7)]);
    [TestMethod]
    public async Task PairSelectionIsExplicitAndClearsWithTheDevice()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath(),PersistSettings:false));
            try
            {
                Assert.IsFalse(window.Control<ComboBox>("PairInput").IsEnabled);
                var outputs = window.Control<ComboBox>("OutputInput"); outputs.ItemsSource = new object[] {"No device",Device};
                outputs.SelectedIndex = 1;
                var pairs = window.Control<ComboBox>("PairInput"); Assert.AreEqual(2,pairs.ItemCount); Assert.IsTrue(pairs.IsEnabled);
                pairs.SelectedIndex = 1;
                StringAssert.Contains(window.Control<TextBlock>("PairDescription").Text!,"Phones 1L");
                Assert.IsFalse(window.Controller.Connected); Assert.IsFalse(window.Control<Button>("ListenButton").IsEnabled);
                outputs.SelectedIndex = 0; Assert.AreEqual(0,pairs.ItemCount); Assert.IsFalse(pairs.IsEnabled);
                outputs.SelectedIndex = 1; Assert.AreEqual(0,pairs.SelectedIndex); // do not carry a phones choice to another device
            }
            finally { await window.Controller.DisposeAsync(); window.Close(); }
        });
    }
    [TestMethod]
    public async Task MeterDistinguishesMutedDisconnectedSilentMonitorAndPhysicalPair()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath(),PersistSettings:false));
            var output = new PlaybackState(true,true,48000,false,1024,48000,0,48000,0,0,0,0,0,.2,true,0,3072,0,PlaybackFault.None,1,
                new(1,.2,.1,.1,.05),10);
            try
            {
                window.UpdateMeter(output);
                Assert.AreEqual(-20,window.Control<ProgressBar>("LeftMeter").Value,.0001);
                StringAssert.Contains(window.Control<TextBlock>("MeterStatus").Text!,"channels 11–12");
                window.UpdateMeter(output with { Muted = true });
                Assert.AreEqual(-96,window.Control<ProgressBar>("LeftMeter").Value);
                StringAssert.Contains(window.Control<TextBlock>("MeterStatus").Text!,"MUTED");
                window.UpdateMeter(output with { Physical = false });
                StringAssert.Contains(window.Control<TextBlock>("MeterStatus").Text!,"no speakers");
                window.UpdateMeter(output with { ClippedSamples = 1 });
                StringAssert.Contains(window.Control<TextBlock>("MeterStatus").Text!,"CLIP detected");
                window.UpdateMeter(null); Assert.AreEqual(-96,window.Control<ProgressBar>("RightMeter").Value);
            }
            finally { await window.Controller.DisposeAsync(); window.Close(); }
        });
    }
    [TestMethod,TestCategory("Native")]
    public async Task ListeningPresetAndSelectedPairReachTheOwnerWithoutHardwareAndReconnectMuted()
    {
        var native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(native)) Assert.Inconclusive("Requires native libraries; all inputs/outputs are fixtures.");
        await UiTestHost.RunAsync(async () =>
        {
            await using var simulator = G2Simulator.Open(new(BasePort:0)); PlaybackDevice? selected = null;
            var controller = new PreviewController((directory,device) => { selected = device; return PlaybackOutput.OpenNull(directory); },
                (directory,request,token) => P2ReceiveSession.Open(directory,new(simulator.BasePort,FrequencyHz:request.FrequencyHz),token));
            var target = new G2RadioTarget("169.254.47.65","169.254.187.120","2c:cf:67:fc:a3:df");
            var window = new MainWindow(new(native,PersistSettings:false,G2Target:target),controller); window.Show();
            try
            {
                window.Control<ComboBox>("OutputInput").ItemsSource = new object[] {"No device",Device};
                window.Control<ComboBox>("OutputInput").SelectedIndex = 1;
                window.Control<ComboBox>("PairInput").SelectedIndex = 1;
                for (int cycle = 0; cycle < 2; ++cycle)
                {
                    window.Control<CheckBox>("ConfirmHardwareInput").IsChecked = true; await window.Connect();
                    Assert.IsTrue(controller.Connected,window.Control<TextBlock>("StatusText").Text);
                    Assert.AreEqual(10,selected!.FirstOutputChannel);
                    Assert.IsTrue(controller.Settings.Muted); Assert.IsTrue(controller.Settings.AudioGainDb <= -40);
                    Assert.IsTrue(window.Control<ComboBox>("PairInput").IsEnabled);
                    int frequency = controller.Settings.FrequencyHz;
                    window.Control<TextBox>("FrequencyInput").Text = (frequency+1000).ToString();
                    window.Control<Slider>("GainInput").Value = -15;
                    StringAssert.Contains(window.Control<TextBlock>("GainText").Text!,"Apply needed");
                    await window.StartListening();
                    Assert.AreEqual(frequency,controller.Settings.FrequencyHz); // no implicit draft retune
                    Assert.AreEqual(-10,controller.Settings.AudioGainDb); Assert.AreEqual(80,controller.Settings.AgcMaxGainDb);
                    Assert.IsFalse(controller.Settings.Muted); Assert.IsFalse(window.Control<Button>("ListenButton").IsEnabled);
                    var clock = Stopwatch.StartNew();
                    while (controller.Snapshot?.Output.Levels?.Sequence is not > 0 && clock.Elapsed.TotalSeconds < 5) await Task.Delay(25);
                    Assert.IsTrue(controller.Snapshot?.Output.Levels?.Sequence > 0);
                    using var bitmap = window.CaptureRenderedFrame(); Assert.IsNotNull(bitmap);
                    await controller.DisconnectAsync();
                    Assert.IsTrue(controller.Settings.Muted); Assert.IsFalse(controller.Connected);
                }
            }
            finally { await controller.DisposeAsync(); window.Close(); }
        });
    }
    [TestMethod]
    public async Task AutomatedCampaignCannotUseListeningAction()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath(),Smoke:true,PersistSettings:false));
            try { await window.StartListening(); Assert.IsFalse(window.Controller.Connected); StringAssert.Contains(window.Control<TextBlock>("StatusText").Text!,"Automated campaigns"); }
            finally { await window.Controller.DisposeAsync(); window.Close(); }
        });
    }
}
