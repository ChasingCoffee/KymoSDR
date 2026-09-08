using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
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
    private bool busy, closing, mayClose, handlingFault;
    internal bool Busy => busy;
    internal long DisplayedFrames => Control<SpectrumView>("SpectrumDisplay").FramesDisplayed;
    public MainWindow() : this(new(PreviewLaunchOptions.DefaultDirectory)) { }
    public MainWindow(PreviewLaunchOptions options) : this(options,new()) { }
    internal MainWindow(PreviewLaunchOptions options,PreviewController controller)
    {
        this.options = options; Controller = controller; AvaloniaXamlLoader.Load(this);
        Control<TextBox>("NativeDirectoryInput").Text = options.NativeDirectory;
        Control<ComboBox>("OutputInput").ItemsSource = outputs; Control<ComboBox>("OutputInput").SelectedIndex = 0;
        Control<Slider>("GainInput").PropertyChanged += (_,e) =>
        { if (e.Property == Slider.ValueProperty) Control<TextBlock>("GainText").Text = $"AF GAIN · {Control<Slider>("GainInput").Value:F0} dB"; };
        timer.Tick += (_,_) => RefreshSnapshot(); timer.Start();
        Closing += OnClosing;
        Closed += (_,_) => { timer.Stop(); Control<SpectrumView>("SpectrumDisplay").Dispose(); };
        if (options.Smoke) Opened += async (_,_) => await RunSmoke();
    }
    internal T Control<T>(string name) where T : Control => this.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing UI control {name}");
    private void Status(string message) => Control<TextBlock>("StatusText").Text = message;
    private void Enabled()
    {
        bool connected = Controller.Connected;
        foreach (string name in new[] {"ConnectButton","RefreshButton","ProtocolInput","OutputInput","NativeDirectoryInput"})
            Control<Control>(name).IsEnabled = !busy && !connected && !closing;
        Control<Button>("DisconnectButton").IsEnabled = (connected || busy) && !closing;
        Control<Button>("ApplyButton").IsEnabled = connected && !busy && !closing;
        Control<CheckBox>("MuteInput").IsEnabled = connected && !busy && !closing;
    }
    private async Task Operation(Func<Task> operation,string progress,string success)
    {
        if (busy || closing) return;
        busy = true; Enabled(); Status(progress);
        try { await operation(); Status(success); }
        catch (OperationCanceledException) { Status("Operation cancelled; simulator/output stopped."); }
        catch (Exception ex) { Status(ex.Message); }
        finally { busy = false; Enabled(); }
    }
    private async void OnConnect(object? sender,RoutedEventArgs e) => await Connect();
    internal Task Connect() => Operation(async () =>
    {
        int protocol = Control<ComboBox>("ProtocolInput").SelectedIndex == 0 ? 2 : 1;
        string directory = Control<TextBox>("NativeDirectoryInput").Text ?? "";
        var device = Control<ComboBox>("OutputInput").SelectedItem as PlaybackDevice;
        await Controller.ConnectAsync(directory,protocol,device);
        Control<CheckBox>("MuteInput").IsChecked = true;
        Control<Slider>("GainInput").Value = Controller.Settings.AudioGainDb;
        Control<SpectrumView>("SpectrumDisplay").Reset();
    },"Starting owned simulator and receiver…","Connected to simulator. Output muted; adjust controls, then unmute when ready.");
    private async void OnDisconnect(object? sender,RoutedEventArgs e)
    {
        if (closing) return;
        try
        {
            await Controller.DisconnectAsync();
            Status("Disconnected. No radio or audio stream is open.");
        }
        catch (Exception ex) { Status($"Shutdown error: {ex.Message}"); }
        finally { Control<CheckBox>("MuteInput").IsChecked = true; Enabled(); }
    }
    private async void OnRefresh(object? sender,RoutedEventArgs e) => await Operation(async () =>
    {
        string directory = Control<TextBox>("NativeDirectoryInput").Text ?? "";
        var devices = await Task.Run(() => PlaybackOutput.EnumerateDevices(directory));
        outputs.Clear(); outputs.Add("No device (silent monitor)"); outputs.AddRange(devices);
        Control<ComboBox>("OutputInput").ItemsSource = outputs.ToArray(); Control<ComboBox>("OutputInput").SelectedIndex = 0;
    },"Enumerating output devices (no stream opened)…","Output list refreshed. Select a device before connecting; no device remains selected by default.");
    private async void OnApply(object? sender,RoutedEventArgs e) => await Apply();
    private async void OnInputKey(object? sender,KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await Apply(); } }
    private Task Apply() => Operation(() => Controller.ApplyAsync(ReadSettings()),"Applying receive controls…","Receive controls applied. Spectrum levels are uncalibrated, not dBm.");
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
    private async void RefreshSnapshot()
    {
        if (closing) return;
        if (Controller.Error is { } fault && !handlingFault)
        {
            handlingFault = true;
            try
            {
                await Controller.DisconnectAsync();
                Control<CheckBox>("MuteInput").IsChecked = true;
                // A removed/reordered device is never silently selected again.
                outputs.Clear(); outputs.Add("No device (silent monitor)");
                Control<ComboBox>("OutputInput").ItemsSource = outputs.ToArray();
                Control<ComboBox>("OutputInput").SelectedIndex = 0;
                Status($"Stopped safely: {fault.Message}"); Enabled();
            }
            catch (Exception ex) { Status($"Shutdown error: {ex.Message}"); }
            finally { handlingFault = false; }
            return;
        }
        var snapshot = Controller.Snapshot; if (snapshot is null) return;
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
    private async void OnClosing(object? sender,WindowClosingEventArgs e)
    {
        if (mayClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; timer.Stop(); Enabled(); Status("Stopping output and joining receiver…");
        await Task.Yield(); // leave the original Closing event before issuing Close again
        try { await Controller.DisposeAsync(); }
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
