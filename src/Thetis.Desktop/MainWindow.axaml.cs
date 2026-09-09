using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Desktop;

public partial class MainWindow : Window
{
    internal PreviewController Controller { get; }
    private readonly PreviewLaunchOptions options;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly List<object> outputs = ["No device (silent monitor)"];
    private readonly PreviewPreferencesStore? preferencesStore;
    private PreviewPreferences preferences = PreviewPreferences.Default;
    private string? preferencesWarning;
    private bool initializeAudioOnOpen;
    private double normalWidth = 1140,normalHeight = 800;
    private bool wasMaximized;
    private bool busy, closing, mayClose, handlingFault;
    private PlaybackSnapshot? completedSnapshot;
    internal bool Busy => busy;
    internal long DisplayedFrames => Control<SpectrumView>("SpectrumDisplay").FramesDisplayed;
    public MainWindow() : this(new(PreviewLaunchOptions.DefaultDirectory)) { }
    public MainWindow(PreviewLaunchOptions options) : this(options,new()) { }
    internal MainWindow(PreviewLaunchOptions options,PreviewController controller,PreviewPreferencesStore? store = null,ConnectionLookups? lookups = null)
    {
        this.options = options; Controller = controller; connectionLookups = lookups ?? ConnectionLookups.Default; AvaloniaXamlLoader.Load(this);
        preferencesStore = options.Automated || !options.PersistSettings ? null : store ?? new(PreviewPreferencesStore.DefaultPath);
        if (preferencesStore is not null)
        {
            var loaded = preferencesStore.Load(); preferences = loaded.Value; preferencesWarning = loaded.Warning;
            initializeAudioOnOpen = loaded.CanSave && (preferences.Audio is not null || !preferences.AudioSelectionInitialized);
            RestorePreferences();
            if (loaded.Warning is not null) Status(loaded.Warning);
        }
        Control<TextBox>("NativeDirectoryInput").Text = options.NativeDirectory;
        Control<ComboBox>("OutputInput").ItemsSource = outputs; Control<ComboBox>("OutputInput").SelectedIndex = 0;
        Control<ComboBox>("OutputInput").SelectionChanged += (_,_) => OutputChanged();
        Control<ComboBox>("PairInput").SelectionChanged += (_,_) => PairChanged();
        Control<ComboBox>("ProtocolInput").SelectionChanged += (_,_) => SourceChanged();
        Control<ComboBox>("DiscoveredRadioInput").SelectionChanged += (_,_) => DiscoverySelected();
        foreach (string field in new[] {"EthernetInput","RadioInput","MacInput","DurationInput"})
            Control<TextBox>(field).TextChanged += (_,_) => HardwareFieldEdited();
        if ((options.G2Target ?? preferences.Connection?.LastG2) is { } target)
        {
            SetG2Form(target,preferences.Connection?.DurationSeconds ?? 60);
            if (options.G2Target is not null || preferences.Connection?.UseG2 == true)
                Control<ComboBox>("ProtocolInput").SelectedIndex = 2;
        }
        Control<Slider>("GainInput").PropertyChanged += (_,e) =>
        { if (e.Property == Slider.ValueProperty) UpdateGainText(); };
        timer.Tick += (_,_) => RefreshSnapshot(); timer.Start();
        Closing += OnClosing;
        Closed += (_,_) => { timer.Stop(); Control<SpectrumView>("SpectrumDisplay").Dispose(); };
        PropertyChanged += (_,e) =>
        {
            if (e.Property == BoundsProperty && WindowState == WindowState.Normal && Bounds.Width >= 900 && Bounds.Height >= 700)
            { normalWidth = Math.Clamp(Bounds.Width,900,3840); normalHeight = Math.Clamp(Bounds.Height,700,2160); }
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized) wasMaximized = WindowState == WindowState.Maximized;
        };
        Opened += (_,_) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
            {
                Width = Math.Clamp(Width,MinWidth,Math.Max(MinWidth,screen.WorkingArea.Width/screen.Scaling));
                Height = Math.Clamp(Height,MinHeight,Math.Max(MinHeight,screen.WorkingArea.Height/screen.Scaling));
            }
            if (preferences.Window.Maximized) WindowState = WindowState.Maximized;
        };
        if (options.Smoke) Opened += async (_,_) => await RunSmoke();
        if (options.EnduranceSeconds > 0) Opened += async (_,_) => await RunEndurance();
        if (!options.Automated && initializeAudioOnOpen) Opened += async (_,_) => await RefreshOutputs();
    }
    internal T Control<T>(string name) where T : Control => this.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing UI control {name}");
    private void Status(string message) => Control<TextBlock>("StatusText").Text = preferencesWarning is null ? message : $"{message} Settings: {preferencesWarning}";
    private bool HardwareSelected => Control<ComboBox>("ProtocolInput").SelectedIndex == 2;
    private void SourceChanged()
    {
        if (HardwareSelected && (options.Automated || enduranceRunning))
        {
            Control<ComboBox>("ProtocolInput").SelectedIndex = 0;
            Status("Automated simulator campaigns cannot select a physical radio."); return;
        }
        bool hardware = HardwareSelected;
        Control<StackPanel>("HardwarePanel").IsVisible = hardware;
        Control<CheckBox>("ConfirmHardwareInput").IsChecked = false;
        Control<Button>("ConnectButton").Content = hardware ? "Connect G2 · RX only" : "Connect simulator";
        Control<TextBlock>("SafetyBanner").Text = hardware ? "G2 · ANT1 RX ONLY · NO TX" : "SIMULATOR · NO RF / TX";
        Control<TextBlock>("SourceDescription").Text = hardware ?
            "Explicit Ethernet/MAC check. Stops after 60 seconds maximum. PA-disable is not a physical interlock." :
            "Owned loopback signal at 14.200 MHz. No LAN discovery.";
        if (hardware && !Controller.Connected) SetFt8Preset();
        if (!hardware) Control<SpectrumView>("SpectrumDisplay").SetView(0,-100,0);
        Enabled();
    }
    private void OutputChanged()
    {
        bool previous = populatingAudio; populatingAudio = true;
        try
        {
            var pairs = Control<ComboBox>("PairInput");
            pairs.ItemsSource = (Control<ComboBox>("OutputInput").SelectedItem as PlaybackDevice)?.Pairs;
            pairs.SelectedIndex = pairs.ItemCount > 0 ? 0 : -1;
            OutputChoiceLabels(); Enabled();
        }
        finally { populatingAudio = previous; }
        RememberAudioChoice();
    }
    private void PairChanged()
    { OutputChoiceLabels(); RememberAudioChoice(); }
    private void OutputChoiceLabels()
    {
        var pair = Control<ComboBox>("PairInput").SelectedItem as PlaybackPair;
        Control<TextBlock>("PairDescription").Text = pair is null ? "No physical output selected." : pair.ToString();
        ToolTip.SetTip(Control<ComboBox>("PairInput"),pair?.ToString());
    }
    private void UpdateGainText()
    {
        int draft = (int)Control<Slider>("GainInput").Value;
        bool pending = Controller.Connected && draft != Controller.Settings.AudioGainDb;
        Control<TextBlock>("GainText").Text = $"AF · {draft} dB · {(pending ? "Apply needed" : "Apply to change")}";
        ToolTip.SetTip(Control<Slider>("GainInput"),Controller.Connected
            ? $"Applied AF: {Controller.Settings.AudioGainDb} dB. Slider edits require Apply controls; mute uses the applied gain."
            : "Connect starts muted at AF −40 dB or lower. Start listening is an explicit action.");
    }
    private void SetFt8Preset()
    {
        Control<TextBox>("FrequencyInput").Text = "14074000";
        Control<ComboBox>("ModeInput").SelectedIndex = 0;
        Control<TextBox>("LowInput").Text = "100"; Control<TextBox>("HighInput").Text = "3000";
        Control<TextBlock>("FrequencyText").Text = "14.074000";
        Control<TextBlock>("ModeText").Text = "USB · 100–3000 Hz";
        Control<SpectrumView>("SpectrumDisplay").SetView(12000,-150,-50);
    }
    private void OnFt8Preset(object? sender,RoutedEventArgs e) { SetFt8Preset(); Status("14.074 MHz USB preset selected. Apply controls if connected; this does not identify or decode FT8."); }
    private void Enabled()
    {
        bool connected = Controller.Connected;
        foreach (string name in new[] {"ConnectButton","ProtocolInput","NativeDirectoryInput",
            "EthernetInput","RadioInput","MacInput","DurationInput","ConfirmHardwareInput"})
            Control<Control>(name).IsEnabled = !busy && !connected && !closing;
        foreach (string name in new[] {"OutputInput","RefreshButton"})
            Control<Control>(name).IsEnabled = !busy && !closing && !options.Automated && !enduranceRunning;
        Control<ComboBox>("PairInput").IsEnabled = !busy && !closing && !options.Automated && !enduranceRunning && Control<ComboBox>("PairInput").ItemCount > 0;
        Control<Button>("SwitchOutputButton").IsEnabled = connected && !busy && !closing && !options.Automated && !enduranceRunning;
        Control<Button>("ForgetOutputButton").IsEnabled = !busy && !connected && !closing && (preferences.Audio is not null || !preferences.AudioSelectionInitialized);
        Control<Button>("DiscoverButton").IsEnabled = HardwareSelected && !busy && !connected && !closing && !options.Automated && !enduranceRunning;
        Control<ComboBox>("DiscoveredRadioInput").IsEnabled = HardwareSelected && !busy && !connected && !closing && Control<ComboBox>("DiscoveredRadioInput").ItemCount > 0;
        Control<Button>("ListenButton").IsEnabled = connected && !busy && !closing && Controller.Settings.Muted && !options.Automated && !enduranceRunning;
        ToolTip.SetTip(Control<Button>("ListenButton"),HardwareSelected
            ? "Explicit G2 listening preset: AF −10 dB, medium AGC maximum 80 dB, then unmute. Uses applied tuning. Check your monitor volume first."
            : "Unmute at the applied gain; does not apply pending tuning or gain edits.");
        Control<Button>("Ft8Button").IsEnabled = !busy && !closing;
        Control<Button>("DisconnectButton").IsEnabled = (connected || busy) && !closing;
        Control<Button>("ApplyButton").IsEnabled = connected && !busy && !closing;
        Control<CheckBox>("MuteInput").IsEnabled = connected && !busy && !closing;
        Control<Button>("ExportButton").IsEnabled = !closing;
    }
    private async Task Operation(Func<Task> operation,string progress,string success)
    {
        if (busy || closing) return;
        busy = true; Enabled(); Status(progress);
        try { await operation(); Status(success); }
        catch (OperationCanceledException) { Status("Operation cancelled; receiver/output stopped."); }
        catch (Exception ex) { Controller.Diagnostics.Event("operation-failed",ex); Status(ex.Message); }
        finally { busy = false; Enabled(); }
    }
    private async void OnConnect(object? sender,RoutedEventArgs e) => await Connect();
    internal Task Connect() => Operation(async () =>
    {
        int protocol = Control<ComboBox>("ProtocolInput").SelectedIndex == 0 ? 2 : 1;
        string directory = Control<TextBox>("NativeDirectoryInput").Text ?? "";
        var device = Control<ComboBox>("OutputInput").SelectedItem as PlaybackDevice;
        if ((options.Automated || enduranceRunning) && device is not null) throw new InvalidOperationException("Automated runs cannot open physical audio.");
        if (device is not null) device = device.WithPair(Control<ComboBox>("PairInput").SelectedItem as PlaybackPair
            ?? throw new ArgumentException("Select a stereo output pair before connecting."));
        if (HardwareSelected)
        {
            if (options.Automated || enduranceRunning) throw new InvalidOperationException("Automated campaigns cannot contact hardware.");
            var initial = ReadSettings();
            if (!int.TryParse(Control<TextBox>("DurationInput").Text,NumberStyles.None,CultureInfo.InvariantCulture,out int seconds))
                throw new ArgumentException("Enter 5–60 receive seconds.");
            var request = new G2HardwareRequest(new(Control<TextBox>("EthernetInput").Text ?? "",
                Control<TextBox>("RadioInput").Text ?? "",Control<TextBox>("MacInput").Text ?? ""),
                initial.FrequencyHz,seconds,Control<CheckBox>("ConfirmHardwareInput").IsChecked == true);
            Control<CheckBox>("ConfirmHardwareInput").IsChecked = false; // consent is never reused or persisted
            await Controller.ConnectG2Async(directory,request,device,initialSettings:initial);
        }
        else await Controller.ConnectAsync(directory,protocol,device,initialSettings:ReadSettings());
        Control<CheckBox>("MuteInput").IsChecked = true;
        Control<Slider>("GainInput").Value = Controller.Settings.AudioGainDb;
        Control<SpectrumView>("SpectrumDisplay").Reset();
        await SavePreferences();
    },HardwareSelected ? "Verifying idle Ethernet G2 and starting ANT1 receive…" : "Starting owned simulator and receiver…",
        HardwareSelected ? "G2 ANT1 RX connected for a bounded test. Output muted; unmute when ready. No TX." :
        "Connected to simulator. Output muted; adjust controls, then unmute when ready.");
    private async void OnDisconnect(object? sender,RoutedEventArgs e)
    {
        if (closing) return;
        try
        {
            await CancelLookup();
            await Controller.DisconnectAsync();
            Status("Disconnected. No radio or audio stream is open.");
        }
        catch (Exception ex) { Status($"Shutdown error: {ex.Message}"); }
        finally { Control<CheckBox>("MuteInput").IsChecked = true; Enabled(); }
    }
    private async void OnRefresh(object? sender,RoutedEventArgs e) => await RefreshOutputs();
    private async void OnApply(object? sender,RoutedEventArgs e) => await Apply();
    private async void OnListen(object? sender,RoutedEventArgs e) => await StartListening();
    internal Task StartListening() => Operation(async () =>
    {
        if (options.Automated || enduranceRunning) throw new InvalidOperationException("Automated campaigns cannot start audible listening.");
        await Controller.StartListeningAsync();
        var value = Controller.Settings;
        Control<CheckBox>("MuteInput").IsChecked = value.Muted;
        Control<Slider>("GainInput").Value = value.AudioGainDb;
        Control<TextBox>("AgcMaxInput").Text = value.AgcMaxGainDb.ToString(CultureInfo.InvariantCulture);
        Control<ComboBox>("AgcInput").SelectedIndex = value.AgcMode switch { ReceiveAgcMode.Off => 0,ReceiveAgcMode.Slow => 1,ReceiveAgcMode.Medium => 2,_ => 3 };
    },"Starting deliberate listening…","Listening started. Watch output levels; slider edits need Apply. Reconnect starts muted again.");
    private async void OnInputKey(object? sender,KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Apply(); } }
    internal Task Apply() => Operation(async () => { await Controller.ApplyAsync(ReadSettings()); await SavePreferences(); },"Applying receive controls…","Receive controls applied. Spectrum levels are uncalibrated, not dBm.");
    private PreviewSettings ReadSettings()
    {
        int Number(string name) => int.TryParse(Control<TextBox>(name).Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out int value)
            ? value : throw new ArgumentException($"Enter a whole number in {name.Replace("Input","")}.");
        var agc = Control<ComboBox>("AgcInput").SelectedIndex switch { 0 => ReceiveAgcMode.Off,1 => ReceiveAgcMode.Slow,2 => ReceiveAgcMode.Medium,_ => ReceiveAgcMode.Fast };
        return new(Number("FrequencyInput"),Control<ComboBox>("ModeInput").SelectedIndex == 0 ? ReceiveMode.Usb : ReceiveMode.Lsb,
            Number("LowInput"),Number("HighInput"),(int)Control<Slider>("GainInput").Value,Control<CheckBox>("MuteInput").IsChecked != false,agc,Number("AgcMaxInput"));
    }
    private async void OnMute(object? sender,RoutedEventArgs e) => await Operation(
        () => Controller.ApplyAsync(Controller.Settings with { Muted = Control<CheckBox>("MuteInput").IsChecked != false }),
        "Updating mute…","Mute updated. Already driver-buffered audio cannot be recalled.");
    private void RestorePreferences()
    {
        var saved = preferences.Receiver;
        Control<ComboBox>("ProtocolInput").SelectedIndex = saved.Protocol == 2 ? 0 : 1;
        Control<TextBox>("FrequencyInput").Text = saved.FrequencyHz.ToString(CultureInfo.InvariantCulture);
        Control<ComboBox>("ModeInput").SelectedIndex = saved.Mode == ReceiveMode.Usb ? 0 : 1;
        Control<TextBox>("LowInput").Text = saved.LowCutHz.ToString(CultureInfo.InvariantCulture);
        Control<TextBox>("HighInput").Text = saved.HighCutHz.ToString(CultureInfo.InvariantCulture);
        Control<ComboBox>("AgcInput").SelectedIndex = saved.AgcMode switch { ReceiveAgcMode.Off => 0,ReceiveAgcMode.Slow => 1,ReceiveAgcMode.Medium => 2,_ => 3 };
        Control<TextBox>("AgcMaxInput").Text = saved.AgcMaxGainDb.ToString(CultureInfo.InvariantCulture);
        Control<CheckBox>("MuteInput").IsChecked = true; Control<Slider>("GainInput").Value = -40;
        normalWidth = Width = preferences.Window.Width; normalHeight = Height = preferences.Window.Height;
        wasMaximized = preferences.Window.Maximized;
        Control<TextBlock>("FrequencyText").Text = (saved.FrequencyHz/1e6).ToString("F6",CultureInfo.InvariantCulture);
        Control<TextBlock>("ModeText").Text = $"{saved.Mode.ToString().ToUpperInvariant()} · {saved.LowCutHz}–{saved.HighCutHz} Hz";
    }
    internal async Task SavePreferences()
    {
        if (preferencesStore is null) return;
        var receive = preferences.Receiver;
        try
        {
            var value = ReadSettings(); value.Validate();
            if (!HardwareSelected) receive = ReceiverPreferences.From(Control<ComboBox>("ProtocolInput").SelectedIndex == 0 ? 2 : 1,value);
        }
        catch (ArgumentException) { /* An invalid draft must not destroy the last valid controls. */ }
        var connection = preferences.Connection ?? new();
        if (HardwareSelected)
        {
            try
            {
                var target = new G2RadioTarget(Control<TextBox>("EthernetInput").Text ?? "",Control<TextBox>("RadioInput").Text ?? "",
                    Control<TextBox>("MacInput").Text ?? "");
                var next = new ConnectionPreferences(target,int.Parse(Control<TextBox>("DurationInput").Text ?? "",CultureInfo.InvariantCulture),true);
                next.Validate(); connection = next;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { /* Retain the last valid target, never consent. */ }
        }
        else connection = connection with { UseG2 = false };
        preferences = new(2,receive,new(normalWidth,normalHeight,wasMaximized),connection,preferences.Audio,preferences.AudioSelectionInitialized);
        try { await preferencesStore.SaveAsync(preferences); preferencesWarning = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { preferencesWarning = ex.Message; Controller.Diagnostics.Event("settings-save-unavailable",ex); }
    }
    private async void OnExport(object? sender,RoutedEventArgs e)
    {
        if (closing) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export KymoSDR session diagnostics",SuggestedFileName = $"KymoSDR-session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("JSON diagnostics") { Patterns = ["*.json"] }]
            });
            if (file is null) return;
            using (file)
            {
                string path = file.TryGetLocalPath() ?? throw new IOException("Choose a local file for an atomic diagnostic export.");
                await ExportDiagnostics(path);
            }
            Status("Diagnostics exported locally. No waveform, device names, network addresses or local paths are included; nothing was uploaded.");
        }
        catch (Exception ex) { Status($"Diagnostic export failed: {ex.Message}"); }
    }
    internal Task ExportDiagnostics(string path)
    {
        if (preferencesStore is not null && string.Equals(Path.GetFullPath(path),preferencesStore.PathName,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Choose a diagnostic report file, not the settings file.");
        Controller.Diagnostics.PublishDisplay(Control<SpectrumView>("SpectrumDisplay").Telemetry);
        return Controller.Diagnostics.ExportAsync(path);
    }
    private async void RefreshSnapshot()
    {
        if (closing) return;
        UpdateGainText();
        Controller.Diagnostics.PublishDisplay(Control<SpectrumView>("SpectrumDisplay").Telemetry);
        if (!Controller.Connected && !busy && Controller.LastSnapshot is { Hardware.StopReason:1 } completed &&
            !ReferenceEquals(completedSnapshot,completed))
        {
            completedSnapshot = completed;
            Control<CheckBox>("MuteInput").IsChecked = true;
            Status("G2 receive test completed; receive/output closed and STOP sent. Confirm ANT1 again to reconnect.");
        }
        Enabled();
        if (Controller.Error is { } fault && !handlingFault)
        {
            handlingFault = true;
            try
            {
                await Controller.DisconnectAsync();
                Control<CheckBox>("MuteInput").IsChecked = true;
                // A removed/reordered device is never silently selected again.
                ResetOutputChoices();
                Control<TextBlock>("AudioRestoreText").Text = "Output stopped. Refresh and review the device/pair before reconnecting.";
                Status($"Stopped safely: {fault.Message}"); Enabled();
            }
            catch (Exception ex) { Status($"Shutdown error: {ex.Message}"); }
            finally { handlingFault = false; }
            return;
        }
        var snapshot = Controller.Snapshot;
        UpdateMeter(snapshot?.Output);
        if (snapshot is null) return;
        var settings = Controller.Settings;
        Control<TextBlock>("FrequencyText").Text = (settings.FrequencyHz/1e6).ToString("F6",CultureInfo.InvariantCulture);
        Control<TextBlock>("ModeText").Text = $"{settings.Mode.ToString().ToUpperInvariant()} · {settings.LowCutHz}–{settings.HighCutHz} Hz";
        Control<TextBlock>("RateText").Text = $"I/Q {snapshot.Receive.InputRate/1000} kHz → audio {snapshot.Output.Rate/1000.0:G4} kHz";
        Control<TextBlock>("PacketText").Text = $"I/Q packets   {snapshot.Receive.IqPackets:N0}";
        Control<TextBlock>("QueueText").Text = $"Audio queue   {snapshot.Output.Queued:N0} / 8,192";
        Control<TextBlock>("ErrorText").Text = $"Gaps / overruns   {snapshot.Receive.MissingPackets} / {snapshot.Receive.InputOverruns}";
        string correction = snapshot.Output.ClockTracking ? $"Clock correction   {snapshot.Output.CorrectionPpm:+0.0;-0.0;0.0} ppm" : "Clock correction   sample-driven";
        Control<TextBlock>("OutputHealthText").Text = $"Underruns / drops   {snapshot.Output.Underruns+snapshot.Output.DriverUnderruns} / {snapshot.Output.Rejected}\n{correction}\n{(snapshot.Output.Physical ? "Device" : "Silent monitor")} · {(settings.Muted ? "muted" : "unmuted")}";
        Control<SpectrumView>("SpectrumDisplay").Update(snapshot.Spectrum);
    }
    internal void UpdateMeter(PlaybackState? output)
    {
        var level = output is { Active:true,Muted:false,Switching:false } ? output.Levels ?? PlaybackLevels.Silence : PlaybackLevels.Silence;
        Control<ProgressBar>("LeftMeter").Value = PlaybackLevels.Decibels(level.LeftRms);
        Control<ProgressBar>("RightMeter").Value = PlaybackLevels.Decibels(level.RightRms);
        static string Peak(double value) => value <= 0 ? "−∞ dBFS" : $"{PlaybackLevels.Decibels(value):F1} dBFS";
        Control<TextBlock>("LeftPeakText").Text = Peak(level.LeftPeak);
        Control<TextBlock>("RightPeakText").Text = Peak(level.RightPeak);
        string state = output is null ? "disconnected" : output.Switching ? "switching output" : !output.Active ? "stopped" : output.Muted ? "MUTED" : output.Physical ?
            $"channels {output.FirstOutputChannel+1}–{output.FirstOutputChannel+2}" : "silent monitor · no speakers";
        var label = Control<TextBlock>("MeterStatus");
        label.Text = $"OUTPUT · {state} · dBFS (RMS bars / peak labels){(output?.ClippedSamples > 0 ? " · CLIP detected" : "")}";
        label.Foreground = output?.ClippedSamples > 0 ? Brushes.OrangeRed : output?.Muted == true ? Brushes.Goldenrod : Brushes.LightSlateGray;
        ToolTip.SetTip(Control<ProgressBar>("LeftMeter"),$"Software L RMS: {PlaybackLevels.Decibels(level.LeftRms):F1} dBFS. Post mute; not hardware monitor readback.");
        ToolTip.SetTip(Control<ProgressBar>("RightMeter"),$"Software R RMS: {PlaybackLevels.Decibels(level.RightRms):F1} dBFS. Post mute; not hardware monitor readback.");
    }
    private async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; timer.Stop(); Enabled(); Status("Stopping output and joining receiver…");
        await Task.Yield(); // leave the original Closing event before issuing Close again
        try { await CancelLookup(); await Controller.DisposeAsync(); await SavePreferences(); }
        catch (Exception ex) { Console.Error.WriteLine($"Shutdown: {ex.Message}"); }
        finally { mayClose = true; Close(); }
    }
    private async Task RunSmoke()
    {
        var clock = Stopwatch.StartNew(); int exit = 4;
        try
        {
            await Connect();
            if (!Controller.Connected) throw new InvalidOperationException(Control<TextBlock>("StatusText").Text);
            await WaitFor(() => Control<SpectrumView>("SpectrumDisplay").FramesRendered >= 5 && DisplayedFrames >= 10 && Controller.Snapshot?.Output.Rendered > 48000);
            if (Controller.Snapshot is not { } s || s.Output.Physical || !s.Output.Muted || s.NullRms != 0) throw new InvalidOperationException("Smoke must use muted no-device output.");
            Control<TextBox>("FrequencyInput").Text = "14198500";
            Control<Button>("ApplyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(() => !Busy && Controller.Snapshot?.Spectrum?.RequestedCenterFrequencyHz == 14_198_500);
            await WaitFor(() => DisplayedFrames >= 20);
            if (options.Screenshot is not null)
            {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width,(int)Bounds.Height),new Vector(96,96));
                bitmap.Render(this); bitmap.Save(options.Screenshot,PngBitmapEncoderOptions.Default);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { passed = true, loopbackOnly = true, physicalAudio = false, framesDisplayed = DisplayedFrames,
                framesRendered = Control<SpectrumView>("SpectrumDisplay").FramesRendered,
                elapsedMilliseconds = clock.ElapsedMilliseconds, receive = Controller.Snapshot!.Receive, output = Controller.Snapshot.Output }));
            exit = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Desktop smoke failed: {ex.Message}"); }
        finally
        {
            try { await Controller.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"Desktop shutdown failed: {ex.Message}"); exit = 4; }
            mayClose = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
            else Close();
        }
    }
    private async Task WaitFor(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (Controller.Error is { } error) throw new InvalidOperationException("Preview pump failed.",error);
            if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException(Control<TextBlock>("StatusText").Text);
            await Task.Delay(25);
        }
    }
}
