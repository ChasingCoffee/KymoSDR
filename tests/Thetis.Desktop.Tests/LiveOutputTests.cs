using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class LiveOutputTests
{
    private string directory = null!,path = null!;
    private static PlaybackDevice Device(int index,bool systemDefault = false,int rates = 7) => new(index,$"Fixture output {index}","Fixture",48000,rates,32,
        [new(0,"Main L","Main R",rates),new(10,"Phones L","Phones R",rates)],IsSystemDefault:systemDefault);
    private static PreviewController NoOutput() => new((_,_) => throw new AssertFailedException("No output stream may open."));
    private static ConnectionLookups Lookups(Func<string,IReadOnlyList<PlaybackDevice>> devices) =>
        new(_ => throw new AssertFailedException("No LAN discovery permitted."),devices);
    [TestInitialize] public void Setup()
    { directory = Path.Combine(Path.GetTempPath(),$"kymo-live-output-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory); path = Path.Combine(directory,"settings.json"); }
    [TestCleanup] public void Cleanup() => Directory.Delete(directory,true);
    [TestMethod]
    public async Task FirstRunSelectsSystemDefaultWithoutOpeningItAndExplicitNoDeviceSticks()
    {
        int enumerations = 0;
        var lookups = Lookups(_ => { ++enumerations; return new[] {Device(3),Device(91,true)}; });
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),NoOutput(),new(path),lookups); window.Show();
            try
            {
                await Wait(() => !window.Busy && enumerations == 1);
                Assert.AreEqual(91,((PlaybackDevice)window.Control<ComboBox>("OutputInput").SelectedItem!).Index);
                Assert.IsFalse(window.Controller.Connected); Assert.IsTrue(window.Controller.Settings.Muted);
                var saved = new PreviewPreferencesStore(path).Load().Value;
                Assert.IsTrue(saved.AudioSelectionInitialized); Assert.AreEqual(Device(91).Name,saved.Audio!.Name);
                window.Control<ComboBox>("OutputInput").SelectedIndex = 0; await window.SavePreferences();
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
            var reopened = new MainWindow(new(Path.GetTempPath()),NoOutput(),new(path),lookups); reopened.Show();
            try
            {
                Assert.AreEqual(0,reopened.Control<ComboBox>("OutputInput").SelectedIndex); Assert.AreEqual(1,enumerations);
                await reopened.RefreshOutputs(); Assert.AreEqual(2,enumerations);
                Assert.AreEqual(0,reopened.Control<ComboBox>("OutputInput").SelectedIndex);
                Assert.IsNull(new PreviewPreferencesStore(path).Load().Value.Audio);
            }
            finally { reopened.Close(); await Wait(() => !reopened.IsVisible); }
        });
    }
    [TestMethod]
    public async Task OldSchemaTwoNoDeviceAndMissingSavedDeviceNeverFallBackToDefault()
    {
        string old = JsonSerializer.Serialize(new {schemaVersion=2,receiver=new ReceiverPreferences(),window=new WindowPreferences(),audio=(object?)null},AtomicJsonFile.Options);
        File.WriteAllText(path,old);
        var store = new PreviewPreferencesStore(path); Assert.IsTrue(store.Load().Value.AudioSelectionInitialized);
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),NoOutput(),new(path),Lookups(_ => [Device(91,true)])); window.Show();
            try { await window.RefreshOutputs(); Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex); }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
        store = new(path); store.Load();
        await store.SaveAsync(PreviewPreferences.Default with {Audio=AudioOutputPreferences.From(Device(3),Device(3).SelectedPair)});
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),NoOutput(),new(path),Lookups(_ => [Device(91,true)])); window.Show();
            try
            {
                await Wait(() => !window.Busy);
                Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                StringAssert.Contains(window.Control<TextBlock>("AudioRestoreText").Text!,"missing, ambiguous or changed");
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
    }
    [TestMethod]
    public async Task UnsupportedSystemDefaultStaysSilentAndAutomationNeverEnumerates()
    {
        int enumerations = 0;
        var lookups = Lookups(_ => { ++enumerations; return new[] {Device(3)}; });
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),NoOutput(),new(path),lookups); window.Show();
            try
            {
                await Wait(() => !window.Busy && enumerations == 1);
                Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                StringAssert.Contains(window.Control<TextBlock>("AudioRestoreText").Text!,"No supported system default");
                await window.ForgetOutput(); Assert.IsTrue(new PreviewPreferencesStore(path).Load().Value.AudioSelectionInitialized);
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
            var automatic = new MainWindow(new(Path.GetTempPath(),Smoke:true),NoOutput(),new(path),lookups);
            try
            {
                await automatic.RefreshOutputs(); await automatic.SwitchOutput();
                Assert.AreEqual(1,enumerations); Assert.IsFalse(automatic.Controller.Connected);
            }
            finally { await automatic.Controller.DisposeAsync(); automatic.Close(); }
        });
    }
    [TestMethod,TestCategory("Native")]
    public async Task ConnectedPickerIsDraftUntilSwitchAndDoesNotApplyPendingRadioControls()
    {
        var native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(native)) Assert.Inconclusive("Native libraries required; all audio outputs are no-device fixtures.");
        await UiTestHost.RunAsync(async () =>
        {
            int opens = 0; PlaybackDevice? selected = null;
            var controller = new PreviewController((directory,device) =>
            { ++opens; selected = device; return PlaybackOutput.OpenNull(directory,device?.PreferredRate ?? 48000); });
            var window = new MainWindow(new(native,PersistSettings:false),controller,null,Lookups(_ => [Device(1,true),Device(2,false,1)])); window.Show();
            try
            {
                await window.RefreshOutputs(); await window.Connect(); Assert.IsTrue(controller.Connected);
                window.Control<Slider>("GainInput").Value = -20; window.Control<CheckBox>("MuteInput").IsChecked = false;
                await window.Apply(); await Wait(() => controller.Snapshot?.Output.Rendered > 4800);
                var controls = controller.Settings; int localPort = controller.Snapshot!.Receive.LocalPort;
                Assert.IsTrue(window.Control<ComboBox>("OutputInput").IsEnabled); Assert.IsTrue(window.Control<ComboBox>("PairInput").IsEnabled);
                window.Control<ComboBox>("OutputInput").SelectedIndex = 2;
                window.Control<ComboBox>("PairInput").SelectedIndex = 1;
                Assert.AreEqual(1,opens); StringAssert.Contains(window.Control<TextBlock>("AudioRestoreText").Text!,"Selection pending");
                window.Control<TextBox>("FrequencyInput").Text = "14197000"; window.Control<Slider>("GainInput").Value = -5;
                await window.SwitchOutput(); Assert.AreEqual(2,opens); Assert.AreEqual(2,selected!.Index); Assert.AreEqual(10,selected.FirstOutputChannel);
                await Wait(() => controller.Snapshot is {Output.Generation:2,Output.Switching:false});
                Assert.AreEqual(44100,controller.Snapshot!.Output.Rate); Assert.AreEqual(localPort,controller.Snapshot.Receive.LocalPort);
                Assert.AreEqual(controls,controller.Settings); Assert.IsTrue(controller.Connected); Assert.IsFalse(controller.Snapshot.Output.Muted);
                window.Control<ComboBox>("OutputInput").SelectedIndex = 0; await window.SwitchOutput();
                Assert.AreEqual(3,opens); Assert.IsNull(selected); Assert.IsTrue(controller.Connected);
                await window.RefreshOutputs(); Assert.AreEqual(4,opens); Assert.IsNull(selected); // refresh keeps silent monitor, not default
                Assert.AreEqual(1L,controller.Diagnostics.Snapshot().SessionsStarted);
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
    }
    private static async Task Wait(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed.TotalSeconds > 10) Assert.Fail("UI did not finish in time."); await Task.Delay(10); }
    }
}
