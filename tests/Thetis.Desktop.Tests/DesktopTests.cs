using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

public static class HeadlessBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().WithInterFont().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
[TestClass,DoNotParallelize]
public sealed class DesktopTests
{
    [TestMethod]
    public void LaunchArgumentsCannotContactHardwareOrUnmute()
    {
        foreach (string[] args in new string[][] { ["--radio","192.0.2.1"], ["--tx"], ["--unmute"], ["--native-dir","relative"], ["--smoke","--smoke"] })
            Assert.ThrowsExactly<ArgumentException>(() => PreviewLaunchOptions.Parse(args));
        var options = PreviewLaunchOptions.Parse(["--native-dir",Path.GetTempPath(),"--smoke"]);
        Assert.IsTrue(options.Smoke); Assert.IsNull(options.Screenshot);
    }
    [TestMethod]
    public async Task InitialWindowIsMutedSilentAccessibleAndResizesWithoutNativeCode()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath())); window.Show();
            try
            {
                Assert.IsFalse(window.Controller.Connected);
                Assert.AreEqual(true,window.Control<CheckBox>("MuteInput").IsChecked);
                Assert.AreEqual(-40,window.Control<Slider>("GainInput").Value);
                Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                Assert.IsFalse(window.Control<Button>("ApplyButton").IsEnabled);
                foreach (var size in new[] {new Size(1140,800),new Size(900,700),new Size(1400,900)})
                {
                    window.Width = size.Width; window.Height = size.Height;
                    using var frame = window.CaptureRenderedFrame(); Assert.IsNotNull(frame);
                    Assert.IsTrue(frame.PixelSize.Width >= 900 && frame.PixelSize.Height >= 700);
                    var plot = window.Control<SpectrumView>("SpectrumDisplay");
                    Assert.IsTrue(plot.Bounds.Height >= 180);
                    Assert.IsTrue(plot.Bounds.Height <= ((Control)plot.Parent!).Bounds.Height);
                    Save(frame,$"initial-{size.Width}.png");
                }
                Assert.IsTrue(window.Control<TextBox>("FrequencyInput").Focus());
                window.KeyTextInput("1");
                Assert.IsFalse(window.Controller.Connected);
            }
            finally
            {
                await window.Controller.DisposeAsync(); window.Close();
                var closeClock = Stopwatch.StartNew();
                while (window.IsVisible && closeClock.Elapsed.TotalSeconds < 10) await Task.Delay(10);
                Assert.IsFalse(window.IsVisible);
            }
        });
    }
    [TestMethod,TestCategory("Native"),DataRow(1),DataRow(2)]
    public async Task ActualControlsConnectTuneApplyMuteAndRenderTheSimulator(int protocol)
    {
        var directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires native receiver/audio libraries; no physical devices.");
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(directory)); window.Show();
            try
            {
                window.Control<ComboBox>("ProtocolInput").SelectedIndex = protocol == 2 ? 0 : 1;
                Click("ConnectButton");
                await Wait(() => !window.Busy && window.Controller.Snapshot?.Spectrum is not null && window.DisplayedFrames >= 3);
                Assert.IsTrue(window.Controller.Snapshot!.Output.Muted);
                Assert.IsFalse(window.Controller.Snapshot.Output.Physical);
                window.Control<TextBox>("FrequencyInput").Text = "14198500";
                window.Control<TextBox>("LowInput").Text = "400";
                window.Control<TextBox>("HighInput").Text = "2200";
                window.Control<ComboBox>("AgcInput").SelectedIndex = 3;
                Click("ApplyButton");
                await Wait(() => !window.Busy && window.Controller.Snapshot?.Spectrum?.RequestedCenterFrequencyHz == 14_198_500);
                Assert.AreEqual(400,window.Controller.Settings.LowCutHz);
                Assert.AreEqual(Thetis.Engine.ReceiveAgcMode.Fast,window.Controller.Settings.AgcMode);
                window.Control<TextBox>("HighInput").Text = "not a number";
                Click("ApplyButton"); await Wait(() => !window.Busy);
                Assert.IsTrue(window.Controller.Connected);
                Assert.AreEqual(2200,window.Controller.Settings.HighCutHz);
                window.Control<TextBox>("HighInput").Text = "2200";
                window.Control<CheckBox>("MuteInput").IsChecked = false; Click("MuteInput");
                await Wait(() => !window.Busy && window.Controller.Snapshot?.NullRms > .001);
                Assert.IsFalse(window.Controller.Snapshot!.Output.Physical);
                long before = window.DisplayedFrames;
                await Wait(() => window.DisplayedFrames >= before+30);
                using var bitmap = window.CaptureRenderedFrame(); Assert.IsNotNull(bitmap);
                Save(bitmap,$"receiver-p{protocol}.png");
                Click("DisconnectButton"); await Wait(() => !window.Controller.Connected);
                Click("ConnectButton"); await Wait(() => !window.Busy && window.Controller.Snapshot?.Output.Rendered > 48000);
                Assert.IsTrue(window.Controller.Settings.Muted); Assert.IsTrue(window.Controller.Settings.AudioGainDb <= -40);
                window.Close(); await Wait(() => !window.IsVisible);
                Assert.IsFalse(window.Controller.Connected);
            }
            finally { await window.Controller.DisposeAsync(); window.Close(); }
            void Click(string name) => window.Control<Control>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            async Task Wait(Func<bool> ready)
            {
                var clock = Stopwatch.StartNew();
                while (!ready())
                {
                    if (clock.Elapsed.TotalSeconds > 20) Assert.Fail(window.Control<TextBlock>("StatusText").Text ?? "Desktop timed out.");
                    await Task.Delay(25);
                }
            }
        });
    }
    [TestMethod,TestCategory("Native")]
    public async Task LostOutputClearsSelectionAndRequiresExplicitMutedReconnect()
    {
        string? directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires native libraries; no physical devices.");
        await UiTestHost.RunAsync(async () =>
        {
            PlaybackOutput? output = null;
            var controller = new PreviewController((path,_) => output = PlaybackOutput.OpenNull(path));
            var window = new MainWindow(new(directory),controller); window.Show();
            try
            {
                // UI selection is a fixture; the injected factory can only open
                // a no-device owner. No enumeration or physical stream is used.
                window.Control<ComboBox>("OutputInput").ItemsSource = new object[] {"No device",new PlaybackDevice(99,"Fixture output","Fixture",48000,2)};
                window.Control<ComboBox>("OutputInput").SelectedIndex = 1;
                await window.Connect();
                await Wait(() => controller.Snapshot?.Output.Rendered > 4800);
                await controller.ApplyAsync(controller.Settings with { Muted = false,AudioGainDb = -20 });
                output!.InterruptNull();
                await Wait(() => !controller.Connected && window.Control<TextBlock>("StatusText").Text?.StartsWith("Stopped safely:",StringComparison.Ordinal) == true);
                Assert.IsTrue(window.Control<Button>("RefreshButton").IsEnabled);
                Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                Assert.AreEqual(1,window.Control<ComboBox>("OutputInput").ItemCount);
                Assert.AreEqual(true,window.Control<CheckBox>("MuteInput").IsChecked);
                Assert.IsFalse(window.Control<Button>("ApplyButton").IsEnabled);
                await window.Connect();
                await Wait(() => controller.Snapshot?.Output.Rendered > 4800);
                Assert.IsTrue(controller.Settings.Muted); Assert.IsTrue(controller.Settings.AudioGainDb <= -40);
                Assert.IsTrue(controller.Snapshot!.Output.Muted); Assert.IsFalse(controller.Snapshot.Output.Physical);
                Assert.IsNull(controller.Error);
            }
            finally { await controller.DisposeAsync(); window.Close(); }
            async Task Wait(Func<bool> ready)
            {
                var clock = Stopwatch.StartNew();
                while (!ready())
                {
                    if (clock.Elapsed.TotalSeconds > 20) Assert.Fail(window.Control<TextBlock>("StatusText").Text ?? "Fault recovery timed out.");
                    await Task.Delay(25);
                }
            }
        });
    }
    [TestMethod,TestCategory("Native")]
    public async Task CloseDuringStartupJoinsTheOwnedSession()
    {
        string? directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) Assert.Inconclusive("Requires native libraries; no physical devices.");
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(directory)); window.Show();
            var connect = window.Connect(); window.Close();
            await connect;
            var clock = Stopwatch.StartNew();
            while (window.IsVisible && clock.Elapsed.TotalSeconds < 20) await Task.Delay(25);
            Assert.IsFalse(window.IsVisible); Assert.IsFalse(window.Controller.Connected);
            await window.Controller.DisposeAsync();
        });
    }
    private static void Save(Bitmap bitmap,string name)
    {
        string? artifacts = Environment.GetEnvironmentVariable("THETIS_DESKTOP_CAPTURE_DIR");
        if (artifacts is not null) { Directory.CreateDirectory(artifacts); bitmap.Save(Path.Combine(artifacts,name),PngBitmapEncoderOptions.Default); }
    }
}
