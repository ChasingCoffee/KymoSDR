using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Thetis.Audio;
using Thetis.Preview;

namespace Thetis.Desktop;

// Injectable metadata-only operations. Tests never enumerate physical devices
// or broadcast to a LAN, including when exercising saved selections.
internal sealed record ConnectionLookups(Func<CancellationToken,G2DiscoveryResult> Discover,
    Func<string,IReadOnlyList<PlaybackDevice>> AudioDevices)
{
    public static ConnectionLookups Default { get; } = new(G2Discovery.Scan,PlaybackOutput.EnumerateDevices);
}

public partial class MainWindow
{
    private readonly ConnectionLookups connectionLookups;
    private bool populatingAudio,populatingTarget;
    private CancellationTokenSource? lookupCancellation;
    private Task? lookupWork;

    private async Task<T> Lookup<T>(Func<CancellationToken,T> lookup)
    {
        using var cancel = new CancellationTokenSource(); lookupCancellation = cancel;
        var work = Task.Run(() => lookup(cancel.Token),cancel.Token); lookupWork = work;
        try { var result = await work; cancel.Token.ThrowIfCancellationRequested(); return result; }
        finally { lookupWork = null; lookupCancellation = null; }
    }
    private async Task CancelLookup()
    {
        lookupCancellation?.Cancel();
        if (lookupWork is { } work)
        {
            try { await work; }
            catch (Exception) { /* Operation reports lookup errors; close still joins ownership. */ }
        }
    }
    private void SetG2Form(G2RadioTarget target,int duration)
    {
        populatingTarget = true;
        try
        {
            Control<TextBox>("EthernetInput").Text = target.LocalAddress;
            Control<TextBox>("RadioInput").Text = target.RadioAddress;
            Control<TextBox>("MacInput").Text = target.MacAddress;
            Control<TextBox>("DurationInput").Text = duration.ToString(CultureInfo.InvariantCulture);
        }
        finally { populatingTarget = false; }
        Control<CheckBox>("ConfirmHardwareInput").IsChecked = false;
    }
    private async void DiscoverySelected()
    {
        if (busy || closing || Control<ComboBox>("DiscoveredRadioInput").SelectedItem is not DiscoveredG2 choice) return;
        ToolTip.SetTip(Control<ComboBox>("DiscoveredRadioInput"),choice.ToString());
        SetG2Form(choice.Target,preferences.Connection?.DurationSeconds ?? 60);
        Control<TextBlock>("DiscoveryStatus").Text = choice.Busy
            ? "Selected G2 was busy. Close its other client before connecting. Fresh idle verification is required."
            : "Selection filled in below. Confirm ANT1, then Connect; identity and idle state will be checked again.";
        await SavePreferences();
    }
    private void HardwareFieldEdited()
    {
        Control<CheckBox>("ConfirmHardwareInput").IsChecked = false;
        if (!populatingTarget) Control<ComboBox>("DiscoveredRadioInput").SelectedIndex = -1;
    }
    private async void OnDiscover(object? sender,RoutedEventArgs e) => await DiscoverRadios();
    internal Task DiscoverRadios() => Operation(async () =>
    {
        if (options.Automated || enduranceRunning || !HardwareSelected || Controller.Connected)
            throw new InvalidOperationException("Discovery is available only for an explicit, disconnected G2 source; never in automated campaigns.");
        Control<CheckBox>("ConfirmHardwareInput").IsChecked = false;
        Control<ComboBox>("DiscoveredRadioInput").ItemsSource = null;
        Control<TextBlock>("DiscoveryStatus").Text = "Scanning Ethernet for up to 4 seconds. No receive start or TX.";
        G2DiscoveryResult result;
        try { result = await Lookup(connectionLookups.Discover); }
        catch
        {
            Control<TextBlock>("DiscoveryStatus").Text = "Discovery cancelled or failed. No connection started; retry when ready.";
            throw;
        }
        Control<ComboBox>("DiscoveredRadioInput").ItemsSource = result.Radios;
        Control<ComboBox>("DiscoveredRadioInput").SelectedIndex = -1; // even one result needs an explicit choice
        Control<TextBlock>("DiscoveryStatus").Text = result.Status;
    },"Discovering G2 radios on Ethernet only…","Discovery finished. Choose a result or use the manual connection fields; nothing is connected.");

    private void ResetOutputChoices()
    {
        populatingAudio = true;
        try
        {
            outputs.Clear(); outputs.Add("No device (silent monitor)");
            Control<ComboBox>("OutputInput").ItemsSource = outputs.ToArray();
            Control<ComboBox>("OutputInput").SelectedIndex = 0;
        }
        finally { populatingAudio = false; }
    }
    internal Task RefreshOutputs() => Operation(async () =>
    {
        if (options.Automated || enduranceRunning)
            throw new InvalidOperationException("Automated sessions cannot enumerate physical outputs.");
        bool connected = Controller.Connected;
        string directory = Control<TextBox>("NativeDirectoryInput").Text ?? "";
        ResetOutputChoices();
        Control<TextBlock>("AudioRestoreText").Text = preferences.Audio is null ? "" : "Looking for the saved output and pair…";
        IReadOnlyList<PlaybackDevice> devices;
        try
        {
            devices = connected
                ? await Controller.RefreshOutputsAsync(connectionLookups.AudioDevices)
                : await Lookup(_ => connectionLookups.AudioDevices(directory));
        }
        catch
        {
            Control<TextBlock>("AudioRestoreText").Text = "Output refresh did not finish. No physical output selected; saved choice retained.";
            throw;
        }
        populatingAudio = true;
        try
        {
            outputs.AddRange(devices);
            Control<ComboBox>("OutputInput").ItemsSource = outputs.ToArray();
            Control<ComboBox>("OutputInput").SelectedIndex = 0;
            var bookmark = connected
                ? Controller.CurrentOutput is { } current ? AudioOutputPreferences.From(current,current.SelectedPair) : null
                : preferences.Audio;
            if (bookmark is { } saved)
            {
                var match = saved.Match(devices);
                if (match is not null)
                {
                    Control<ComboBox>("OutputInput").SelectedIndex = 1+devices.ToList().FindIndex(d => d.Index == match.Index);
                    Control<ComboBox>("PairInput").SelectedItem = match.SelectedPair;
                    Control<TextBlock>("AudioRestoreText").Text = connected ? "Output list refreshed; receive stayed connected and the active output was reopened." :
                        "Saved output/pair restored. Nothing opens until Connect; startup stays muted.";
                }
                else Control<TextBlock>("AudioRestoreText").Text = $"Saved output '{saved.Name}' is missing, ambiguous or changed. No physical output selected; choose one or reconnect it and Refresh.";
            }
            else if (!connected && !preferences.AudioSelectionInitialized)
            {
                var defaults = devices.Where(d => d.IsSystemDefault).ToArray();
                if (defaults.Length == 1)
                {
                    var device = defaults[0];
                    Control<ComboBox>("OutputInput").SelectedItem = device;
                    preferences = preferences with { Audio = AudioOutputPreferences.From(device,device.SelectedPair),AudioSelectionInitialized = true };
                    Control<TextBlock>("AudioRestoreText").Text = "System default output selected. Nothing opens until Connect; startup stays muted.";
                }
                else Control<TextBlock>("AudioRestoreText").Text = "No supported system default output found. No device is selected; Refresh to retry.";
            }
        }
        finally { populatingAudio = false; }
        OutputChoiceLabels();
        await SavePreferences();
    },"Refreshing outputs; connected playback briefly pauses…","Output list refreshed. Review the selected device/pair; use Switch output while receiving.");

    private async void OnSwitchOutput(object? sender,RoutedEventArgs e) => await SwitchOutput();
    internal Task SwitchOutput() => Operation(async () =>
    {
        if (options.Automated || enduranceRunning) throw new InvalidOperationException("Automated campaigns cannot switch physical outputs.");
        var device = SelectedOutput();
        await Controller.SwitchOutputAsync(device);
        preferences = preferences with { Audio = device is null ? null : AudioOutputPreferences.From(device,device.SelectedPair),AudioSelectionInitialized = true };
        Control<TextBlock>("AudioRestoreText").Text = $"Active: {DescribeOutput(Controller.CurrentOutput)}. Receive stayed connected; gain/mute preserved.";
        await SavePreferences();
    },"Switching audio output; receive continues…","Output switched. Applied gain and mute state are unchanged; no radio reconnect.");
    private PlaybackDevice? SelectedOutput()
    {
        var device = Control<ComboBox>("OutputInput").SelectedItem as PlaybackDevice;
        return device?.WithPair(Control<ComboBox>("PairInput").SelectedItem as PlaybackPair ?? throw new ArgumentException("Choose a stereo output pair."));
    }
    private static string DescribeOutput(PlaybackDevice? device) => device is null ? "No device (silent monitor)" : $"{device.Name} · {device.SelectedPair}";

    private async void OnForgetOutput(object? sender,RoutedEventArgs e) => await ForgetOutput();
    internal Task ForgetOutput() => Operation(async () =>
    {
        if (Controller.Connected) throw new InvalidOperationException("Disconnect before clearing the saved output.");
        preferences = preferences with { Audio = null,AudioSelectionInitialized = true }; ResetOutputChoices();
        Control<TextBlock>("AudioRestoreText").Text = "Saved output cleared. No device is selected.";
        await SavePreferences();
    },"Clearing saved output selection…","Saved output cleared. Refresh devices to choose an output again.");

    private async void RememberAudioChoice()
    {
        if (populatingAudio || busy || closing) return;
        var device = Control<ComboBox>("OutputInput").SelectedItem as PlaybackDevice;
        var pair = Control<ComboBox>("PairInput").SelectedItem as PlaybackPair;
        if (device is not null && pair is null) return;
        if (Controller.Connected)
        {
            Control<TextBlock>("AudioRestoreText").Text = $"Selection pending: click Switch output. Active: {DescribeOutput(Controller.CurrentOutput)}.";
            return; // Save only after a successful live handoff, not while browsing pairs.
        }
        preferences = preferences with { Audio = device is null ? null : AudioOutputPreferences.From(device,pair!),AudioSelectionInitialized = true };
        Control<TextBlock>("AudioRestoreText").Text = "";
        await SavePreferences();
    }
}
