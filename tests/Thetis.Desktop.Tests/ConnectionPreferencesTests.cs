using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Audio;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class ConnectionPreferencesTests
{
    private string directory = null!,path = null!;
    private static G2RadioTarget Target => new("169.254.47.65","169.254.187.120","2C-CF-67-FC-A3-DF");
    private static PlaybackDevice Device(int index = 4) => new(index,"Fixture 828","Fixture Core Audio",48000,7,32,
        [new(0,"Main L","Main R",7),new(10,"Phones L","Phones R",7)]);
    private static AudioOutputPreferences Audio => AudioOutputPreferences.From(Device(),Device().Pairs[1]);
    private static PreviewPreferences Saved => PreviewPreferences.Default with { Connection = new(Target,30,true),Audio = Audio };
    [TestInitialize] public void Setup()
    { directory = Path.Combine(Path.GetTempPath(),$"kymo-connections-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory); path = Path.Combine(directory,"settings.json"); }
    [TestCleanup] public void Cleanup() => Directory.Delete(directory,true);
    private async Task Seed(PreviewPreferences value)
    { var store = new PreviewPreferencesStore(path); store.Load(); await store.SaveAsync(value); }
    private static PreviewController NoHardware() => new((_,_) => throw new AssertFailedException("No stream may open."),
        (_,_,_) => throw new AssertFailedException("No hardware connection permitted."));
    private static ConnectionLookups NoLookups => new(_ => throw new AssertFailedException("No LAN discovery permitted."),
        _ => []); // metadata-only fixture; never enumerate a real backend

    [TestMethod]
    public async Task SchemaOneMigratesWithoutWritingUntilSaveAndSchemaTwoStoresOnlySelection()
    {
        string old = JsonSerializer.Serialize(new {schemaVersion = 1,receiver = new ReceiverPreferences(),window = new WindowPreferences()},AtomicJsonFile.Options);
        File.WriteAllText(path,old);
        var store = new PreviewPreferencesStore(path); var loaded = store.Load();
        Assert.IsTrue(loaded.CanSave); Assert.AreEqual(2,loaded.Value.SchemaVersion);
        Assert.IsNull(loaded.Value.Connection); Assert.IsNull(loaded.Value.Audio); Assert.AreEqual(old,File.ReadAllText(path));
        await store.SaveAsync(Saved);
        var restored = new PreviewPreferencesStore(path).Load(); Assert.AreEqual(Saved,restored.Value); Assert.IsNull(restored.Warning);
        string json = File.ReadAllText(path);
        foreach (string forbidden in new[] {"confirmAnt1","muted","audioGainDb","connected","firstOutputChannel","index","nativeDirectory","ptt","transmit"})
            Assert.IsFalse(json.Contains(forbidden,StringComparison.OrdinalIgnoreCase),forbidden);
        Assert.IsTrue(restored.Value.Receiver.ToSettings().Muted); Assert.AreEqual(-40,restored.Value.Receiver.ToSettings().AudioGainDb);
        File.WriteAllText(path,old.Replace("\"schemaVersion\": 1","\"schemaVersion\": 1, \"audio\": null"));
        Assert.IsFalse(new PreviewPreferencesStore(path).Load().CanSave);
    }
    [TestMethod]
    public void AudioBookmarkMatchesCurrentIndexOnlyWhenIdentityAndPairAreUnambiguous()
    {
        var match = Audio.Match([Device(91)]); Assert.IsNotNull(match); Assert.AreEqual(91,match.Index); Assert.AreEqual(10,match.FirstOutputChannel);
        Assert.IsNull(Audio.Match([])); Assert.IsNull(Audio.Match([Device(1),Device(2)]));
        Assert.IsNull(Audio.Match([Device() with { HostApi = "Other backend" }]));
        Assert.IsNull(Audio.Match([Device() with { OutputChannels = 16 }]));
        Assert.IsNull(Audio.Match([Device() with { OutputPairs = [new(10,"Different L","Different R",7)] }]));
        Assert.IsNull(Audio.Match([Device() with { OutputPairs = [new(10,"Phones L","Phones R",0)] }]));
        Assert.ThrowsExactly<ArgumentException>(() => (Audio with { FirstChannel = 11 }).Validate());
        Assert.ThrowsExactly<ArgumentException>(() => (Audio with { Name = "" }).Validate());
    }
    [TestMethod]
    public async Task BadBookmarkAndConsentFieldsProtectTheExistingFile()
    {
        foreach (var value in new[] {Saved with { Connection = new(Target,61,true) },
            Saved with { Connection = new(Target with { RadioAddress = "anan-g2.local" },60,true) },
            Saved with { Audio = Audio with { FirstChannel = 32 } }})
        {
            string json = JsonSerializer.Serialize(value,AtomicJsonFile.Options); File.WriteAllText(path,json);
            var store = new PreviewPreferencesStore(path); Assert.IsFalse(store.Load().CanSave);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(PreviewPreferences.Default));
            Assert.AreEqual(json,File.ReadAllText(path));
        }
        string consent = JsonSerializer.Serialize(Saved,AtomicJsonFile.Options).Replace("\"useG2\": true","\"useG2\": true, \"confirmAnt1ReceiveOnly\": true");
        File.WriteAllText(path,consent); Assert.IsFalse(new PreviewPreferencesStore(path).Load().CanSave);
    }
    [TestMethod]
    public async Task StartupRestoresRadioAndAudioMetadataButNeverConnectsOrRestoresConsent()
    {
        await Seed(Saved); int enumerations = 0;
        await UiTestHost.RunAsync(async () =>
        {
            var lookups = NoLookups with { AudioDevices = _ => { Interlocked.Increment(ref enumerations); return new[] {Device(91)}; } };
            var window = new MainWindow(new(Path.GetTempPath()),NoHardware(),new(path),lookups); window.Show();
            try
            {
                await Wait(() => !window.Busy && enumerations == 1);
                Assert.AreEqual(2,window.Control<ComboBox>("ProtocolInput").SelectedIndex);
                Assert.AreEqual(Target.LocalAddress,window.Control<TextBox>("EthernetInput").Text);
                Assert.AreEqual(Target.RadioAddress,window.Control<TextBox>("RadioInput").Text);
                Assert.AreEqual(Target.MacAddress,window.Control<TextBox>("MacInput").Text);
                Assert.AreEqual("30",window.Control<TextBox>("DurationInput").Text);
                Assert.IsFalse(window.Control<CheckBox>("ConfirmHardwareInput").IsChecked == true);
                Assert.AreEqual(91,((PlaybackDevice)window.Control<ComboBox>("OutputInput").SelectedItem!).Index);
                Assert.AreEqual(10,((PlaybackPair)window.Control<ComboBox>("PairInput").SelectedItem!).FirstChannel);
                Assert.IsFalse(window.Controller.Connected); Assert.IsTrue(window.Control<CheckBox>("MuteInput").IsChecked);
                Assert.AreEqual(-40,window.Control<Slider>("GainInput").Value);
                string report = JsonSerializer.Serialize(window.Controller.Diagnostics.Snapshot(),AtomicJsonFile.Options);
                Assert.IsFalse(report.Contains(Target.RadioAddress,StringComparison.Ordinal));
                Assert.IsFalse(report.Contains(Device().Name,StringComparison.Ordinal));
                await window.Connect(); Assert.IsFalse(window.Controller.Connected);
                StringAssert.Contains(window.Control<TextBlock>("StatusText").Text!,"Confirm ANT1");
                window.Width = 900; window.Height = 700;
                using var bitmap = window.CaptureRenderedFrame(); Assert.IsNotNull(bitmap);
                string? captures = Environment.GetEnvironmentVariable("THETIS_DESKTOP_CAPTURE_DIR");
                if (captures is not null) { Directory.CreateDirectory(captures); bitmap.Save(Path.Combine(captures,"remembered-g2-900.png"),PngBitmapEncoderOptions.Default); }
                window.Control<ComboBox>("PairInput").SelectedIndex = 0;
                await window.SavePreferences(); Assert.AreEqual(0,new PreviewPreferencesStore(path).Load().Value.Audio!.FirstChannel);
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
    }
    [TestMethod]
    public async Task MissingAudioBookmarkSurvivesRefreshUntilAnExplicitNoDeviceChoice()
    {
        await Seed(Saved); IReadOnlyList<PlaybackDevice> available = [];
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),NoHardware(),new(path),NoLookups with { AudioDevices = _ => available });
            window.Show(); await Wait(() => !window.Busy);
            try
            {
                await window.RefreshOutputs(); Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                StringAssert.Contains(window.Control<TextBlock>("AudioRestoreText").Text!,"missing, ambiguous or changed");
                await window.SavePreferences(); Assert.AreEqual(Audio,new PreviewPreferencesStore(path).Load().Value.Audio);
                available = [Device(19)]; await window.RefreshOutputs(); Assert.AreEqual(1,window.Control<ComboBox>("OutputInput").SelectedIndex);
                window.Control<ComboBox>("OutputInput").SelectedIndex = 0; await window.SavePreferences();
                Assert.IsNull(new PreviewPreferencesStore(path).Load().Value.Audio);
                await window.RefreshOutputs(); Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                window.Control<ComboBox>("OutputInput").SelectedIndex = 1; await window.SavePreferences();
                available = []; await window.RefreshOutputs(); await window.ForgetOutput();
                Assert.IsNull(new PreviewPreferencesStore(path).Load().Value.Audio);
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
    }
    [TestMethod]
    public async Task DiscoveryRequiresExplicitChoiceAndSelectionClearsConsentWithoutConnecting()
    {
        int scans = 0;
        await UiTestHost.RunAsync(async () =>
        {
            var choices = new[] {new DiscoveredG2(Target,"fixture Ethernet",false),new DiscoveredG2(Target with { RadioAddress = "169.254.187.121",MacAddress = "02-00-00-00-00-02" },"fixture Ethernet",true)};
            var window = new MainWindow(new(Path.GetTempPath(),G2Target:Target),NoHardware(),new(path),NoLookups with
            { Discover = _ => { Interlocked.Increment(ref scans); return new(choices,"Choose a G2."); } });
            window.Show(); await Wait(() => !window.Busy);
            string? beforeDiscovery = File.Exists(path) ? File.ReadAllText(path) : null;
            try
            {
                Assert.AreEqual(0,scans); window.Control<CheckBox>("ConfirmHardwareInput").IsChecked = true;
                await window.DiscoverRadios(); Assert.AreEqual(1,scans);
                Assert.AreEqual(-1,window.Control<ComboBox>("DiscoveredRadioInput").SelectedIndex);
                Assert.AreEqual(beforeDiscovery,File.Exists(path) ? File.ReadAllText(path) : null); // discovery must not save/choose a result
                Assert.IsFalse(window.Control<CheckBox>("ConfirmHardwareInput").IsChecked == true);
                window.Control<ComboBox>("DiscoveredRadioInput").SelectedIndex = 1;
                await window.SavePreferences(); Assert.IsFalse(window.Controller.Connected);
                Assert.AreEqual(choices[1].Target,new PreviewPreferencesStore(path).Load().Value.Connection!.LastG2);
                StringAssert.Contains(window.Control<TextBlock>("DiscoveryStatus").Text!,"busy");
                window.Control<CheckBox>("ConfirmHardwareInput").IsChecked = true;
                window.Control<TextBox>("RadioInput").Text = "169.254.187.122";
                await Wait(() => window.Control<CheckBox>("ConfirmHardwareInput").IsChecked != true);
            }
            finally { window.Close(); await Wait(() => !window.IsVisible); }
        });
    }
    [TestMethod]
    public void DiscoveryPolicyIsEthernetOnlyBoundedAndExcludesUnsupportedProfiles()
    {
        var options = G2Discovery.ScanOptions();
        Assert.IsFalse(options.IncludeWireless); Assert.IsFalse(options.IncludeOtherInterfaceTypes); Assert.IsFalse(options.AllowLoopback);
        Assert.IsFalse(options.IncludeGeneralBroadcast); Assert.IsTrue(options.IncludeEthernet); Assert.IsTrue(options.AllowAPIPA);
        Assert.AreEqual(4000,options.MaxScanMilliseconds); Assert.AreEqual(RadioDiscoveryProtocolMode.P2Only,options.ProtocolMode);
        Assert.IsNull(options.FixedTargetIp); Assert.IsNull(options.FixedLocalIp);
        var radio = new RadioInfo { Protocol = RadioDiscoveryRadioProtocol.P2,DeviceType = (HPSDRHW)10,CodeVersion = 27,
            Protocol2Supported = 43,NumRxs = 10,DiscoveryPortBase = 1024,IpAddress = IPAddress.Parse(Target.RadioAddress),MacAddress = Target.MacAddress };
        var nic = new NicRadioScanResult {NicInterfaceType = NetworkInterfaceType.Ethernet,LocalIPv4 = IPAddress.Parse(Target.LocalAddress),
            LocalMaskIPv4 = IPAddress.Parse("255.255.0.0"),NicName = "fixture",Radios = [radio,radio],Diagnostics = new()};
        var scan = G2Discovery.Describe([nic]); Assert.AreEqual(1,scan.Radios.Count); Assert.IsFalse(scan.Radios[0].Busy);
        radio.IsBusy = true; Assert.IsTrue(G2Discovery.Describe([nic]).Radios[0].Busy);
        nic.Diagnostics.DeadlineReached = true; StringAssert.Contains(G2Discovery.Describe([nic]).Status,"incomplete");
        radio.CodeVersion = 42; Assert.AreEqual(0,G2Discovery.Describe([nic]).Radios.Count);
        radio.CodeVersion = 27; nic.NicInterfaceType = NetworkInterfaceType.Wireless80211;
        Assert.AreEqual(0,G2Discovery.Describe([nic]).Radios.Count);
    }
    [TestMethod]
    public async Task AutomationAndNoSettingsIgnoreSavedHardwareAndAudio()
    {
        await Seed(Saved);
        await UiTestHost.RunAsync(async () =>
        {
            foreach (var options in new[] {new PreviewLaunchOptions(Path.GetTempPath(),Smoke:true),new PreviewLaunchOptions(Path.GetTempPath(),PersistSettings:false)})
            {
                var window = new MainWindow(options,NoHardware(),new(path),NoLookups);
                try
                {
                    Assert.AreEqual(0,window.Control<ComboBox>("ProtocolInput").SelectedIndex);
                    Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                    if (options.Automated) { await window.DiscoverRadios(); await window.RefreshOutputs(); }
                    Assert.IsFalse(window.Controller.Connected); await window.SavePreferences();
                    Assert.AreEqual(Saved,new PreviewPreferencesStore(path).Load().Value);
                }
                finally { await window.Controller.DisposeAsync(); window.Close(); }
            }
        });
    }
    [TestMethod]
    public async Task ClosingCancelsAndJoinsDiscoveryBeforeSavingWithoutLateResults()
    {
        await UiTestHost.RunAsync(async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var window = new MainWindow(new(Path.GetTempPath(),PersistSettings:false,G2Target:Target),NoHardware(),null,NoLookups with
            { Discover = token => { entered.SetResult(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new([],"unreachable"); } });
            window.Show(); var scan = window.DiscoverRadios();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); window.Close();
            await scan.WaitAsync(TimeSpan.FromSeconds(5)); await Wait(() => !window.IsVisible);
            Assert.IsFalse(window.Busy); Assert.IsFalse(window.Controller.Connected);
            Assert.AreEqual(0,window.Control<ComboBox>("DiscoveredRadioInput").ItemCount);
        });
    }
    private static async Task Wait(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done()) { if (clock.Elapsed.TotalSeconds > 10) Assert.Fail("UI did not complete in time."); await Task.Delay(10); }
    }
}
