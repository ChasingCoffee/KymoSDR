using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Desktop.Tests;

[TestClass,DoNotParallelize]
public sealed class PreferencesTests
{
    private string directory = null!,path = null!;
    [TestInitialize]
    public void Setup() { directory = Path.Combine(Path.GetTempPath(),$"kymosdr-prefs-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory); path = Path.Combine(directory,"preview-settings.json"); }
    [TestCleanup]
    public void Cleanup() { Directory.Delete(directory,recursive:true); }

    [TestMethod]
    public async Task VersionedAllowListRoundTripsWithoutRestoringUnsafeState()
    {
        var store = new PreviewPreferencesStore(path); var initial = store.Load();
        Assert.IsTrue(initial.CanSave); Assert.IsFalse(File.Exists(path));
        var saved = new PreviewPreferences(1,new(1,7_101_000,ReceiveMode.Lsb,400,2400,ReceiveAgcMode.Fast,70),new(1250,850,true));
        await store.SaveAsync(saved);
        var loaded = new PreviewPreferencesStore(path).Load(); Assert.AreEqual(saved,loaded.Value); Assert.IsNull(loaded.Warning);
        var receive = loaded.Value.Receiver.ToSettings(); Assert.IsTrue(receive.Muted); Assert.AreEqual(-40,receive.AudioGainDb);
        string json = File.ReadAllText(path);
        foreach (string forbidden in new[] {"muted","audioGainDb","device","nativeDirectory","connected","transmit","ptt"})
            Assert.IsFalse(json.Contains(forbidden,StringComparison.OrdinalIgnoreCase),forbidden);
        Assert.AreEqual(1,Directory.GetFiles(directory).Length);
    }
    [TestMethod,DataRow("corrupt"),DataRow("future"),DataRow("null"),DataRow("invalid"),DataRow("unsafe"),DataRow("oversize")]
    public async Task UnusableSettingsArePreservedAndNeverSilentlyReplaced(string kind)
    {
        string json = JsonSerializer.Serialize(PreviewPreferences.Default,AtomicJsonFile.Options);
        json = kind switch
        {
            "corrupt" => "{unfinished", "future" => json.Replace("\"schemaVersion\": 1","\"schemaVersion\": 99"),
            "null" => JsonSerializer.Serialize(PreviewPreferences.Default with { Window = null! },AtomicJsonFile.Options),
            "invalid" => json.Replace("14199000","-1"), "unsafe" => json.Replace("\"schemaVersion\": 1","\"connected\": true, \"schemaVersion\": 1"),
            _ => new string(' ',65537)
        };
        File.WriteAllText(path,json); byte[] original = File.ReadAllBytes(path);
        var store = new PreviewPreferencesStore(path); var result = store.Load();
        Assert.IsFalse(result.CanSave); Assert.IsNotNull(result.Warning); Assert.AreEqual(PreviewPreferences.Default,result.Value);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.SaveAsync(PreviewPreferences.Default));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(path));
    }
    [TestMethod]
    public async Task ExternalChangesAndUnloadedStoresCannotOverwriteSettings()
    {
        var unloaded = new PreviewPreferencesStore(path);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => unloaded.SaveAsync(PreviewPreferences.Default));
        var store = new PreviewPreferencesStore(path); store.Load(); await store.SaveAsync(PreviewPreferences.Default);
        byte[] external = Encoding.UTF8.GetBytes("external editor content"); File.WriteAllBytes(path,external);
        await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(PreviewPreferences.Default));
        CollectionAssert.AreEqual(external,File.ReadAllBytes(path));
    }
    [TestMethod]
    public async Task ConcurrentSavesStayCompleteAndLegacyFilesAreUntouched()
    {
        string legacy = Path.Combine(directory,"Thetis.xml"); File.WriteAllText(legacy,"legacy calibration — leave unchanged");
        var store = new PreviewPreferencesStore(path); store.Load();
        var second = PreviewPreferences.Default with { Receiver = new(FrequencyHz:7_101_000) };
        await Task.WhenAll(store.SaveAsync(PreviewPreferences.Default),store.SaveAsync(second));
        Assert.AreEqual(second,new PreviewPreferencesStore(path).Load().Value);
        Assert.AreEqual("legacy calibration — leave unchanged",File.ReadAllText(legacy));
        Assert.AreEqual(2,Directory.GetFiles(directory).Length);
    }
    [TestMethod,DataRow(899d,800d),DataRow(1140d,699d),DataRow(3841d,800d),DataRow(double.NaN,800d)]
    public async Task InvalidWindowDimensionsCannotOverwriteValidPreferences(double width,double height)
    {
        var store = new PreviewPreferencesStore(path); store.Load(); await store.SaveAsync(PreviewPreferences.Default);
        byte[] original = File.ReadAllBytes(path);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(PreviewPreferences.Default with { Window = new(width,height) }));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(path));
    }
    [TestMethod]
    public void AtomicPublishFailurePreservesOriginalAndRemovesOnlyOwnedTemporaryFile()
    {
        byte[] original = Encoding.UTF8.GetBytes("original"); File.WriteAllBytes(path,original);
        Assert.ThrowsExactly<IOException>(() => AtomicJsonFile.Write(path,Encoding.UTF8.GetBytes("replacement"),beforePublish:() => throw new IOException("Injected before rename.")));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(path)); Assert.AreEqual(1,Directory.GetFiles(directory).Length);
        using var token = new CancellationTokenSource(); token.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => AtomicJsonFile.Write(path,[],token.Token));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(path)); Assert.AreEqual(1,Directory.GetFiles(directory).Length);
    }
    [TestMethod]
    public async Task WindowRestoresControlsButStartsDisconnectedMutedWithoutOutputSelection()
    {
        var store = new PreviewPreferencesStore(path); store.Load();
        await store.SaveAsync(new(1,new(1,7_101_000,ReceiveMode.Lsb,400,2400,ReceiveAgcMode.Fast,70),new(1250,850)));
        await UiTestHost.RunAsync(async () =>
        {
            var window = new MainWindow(new(Path.GetTempPath()),new(),new(path)); window.Show();
            try
            {
                Assert.IsFalse(window.Controller.Connected); Assert.IsNull(window.Controller.Snapshot);
                Assert.AreEqual("7101000",window.Control<TextBox>("FrequencyInput").Text);
                Assert.AreEqual(1,window.Control<ComboBox>("ModeInput").SelectedIndex);
                Assert.AreEqual(1,window.Control<ComboBox>("ProtocolInput").SelectedIndex);
                Assert.AreEqual(0,window.Control<ComboBox>("OutputInput").SelectedIndex);
                Assert.AreEqual(1,window.Control<ComboBox>("OutputInput").ItemCount);
                Assert.AreEqual(true,window.Control<CheckBox>("MuteInput").IsChecked); Assert.AreEqual(-40,window.Control<Slider>("GainInput").Value);
                Assert.AreEqual(1250,window.Width); Assert.AreEqual(850,window.Height);
                window.Control<TextBox>("FrequencyInput").Text = "invalid draft";
                window.Width = 1300; window.Height = 900; await Task.Delay(100); await window.SavePreferences();
                var saved = new PreviewPreferencesStore(path).Load().Value;
                Assert.AreEqual(7_101_000,saved.Receiver.FrequencyHz); Assert.AreEqual(1300,saved.Window.Width);
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => window.ExportDiagnostics(path));
                window.Control<TextBox>("FrequencyInput").Text = "7102000";
                window.Control<Slider>("GainInput").Value = -10;
                window.Control<CheckBox>("MuteInput").IsChecked = false;
            }
            finally { window.Close(); await WaitClosed(window); }
        });
        var closed = new PreviewPreferencesStore(path).Load().Value;
        Assert.AreEqual(7_102_000,closed.Receiver.FrequencyHz);
        Assert.IsTrue(closed.Receiver.ToSettings().Muted); Assert.AreEqual(-40,closed.Receiver.ToSettings().AudioGainDb);
    }
    [TestMethod,TestCategory("Native")]
    public async Task RestoredControlsAreActuallyUsedByBothProtocolConnections()
    {
        var native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(native)) Assert.Inconclusive("Requires native loopback receive; no audio devices.");
        await UiTestHost.RunAsync(async () =>
        {
            foreach (int protocol in new[] {1,2})
            {
                var store = new PreviewPreferencesStore(path); store.Load();
                await store.SaveAsync(new(1,new(protocol,14_201_000,ReceiveMode.Lsb,400,2400,ReceiveAgcMode.Fast,70),new()));
                var window = new MainWindow(new(native),new(),new(path)); window.Show();
                try
                {
                    await window.Connect();
                    Assert.IsTrue(window.Controller.Connected,window.Control<TextBlock>("StatusText").Text);
                    var value = window.Controller.Settings;
                    Assert.AreEqual(14_201_000,value.FrequencyHz); Assert.AreEqual(ReceiveMode.Lsb,value.Mode);
                    Assert.AreEqual(400,value.LowCutHz); Assert.AreEqual(2400,value.HighCutHz); Assert.AreEqual(ReceiveAgcMode.Fast,value.AgcMode);
                    Assert.IsTrue(value.Muted); Assert.AreEqual(-40,value.AudioGainDb);
                }
                finally { window.Close(); await WaitClosed(window); }
            }
        });
    }
    private static async Task WaitClosed(MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (window.IsVisible) await Task.Delay(25,timeout.Token);
        Assert.IsFalse(window.Controller.Connected);
    }
}
