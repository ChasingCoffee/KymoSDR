using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Thetis.Preview;

namespace Thetis.Desktop;

public sealed record DesktopEndurancePhase(int Protocol,double ConnectedSeconds,long DisplayedFrames,long RenderedFrames,
    long StartWorkingSetBytes,long EndWorkingSetBytes);
public sealed record DesktopEnduranceReport(int SchemaVersion,bool Passed,int RequestedConnectedSeconds,double ElapsedSeconds,
    string? FailureType,string Qualification,DesktopEndurancePhase[] Phases,DiagnosticReport Diagnostics);

public partial class MainWindow
{
    private bool enduranceRunning;
    // This campaign is deliberately distinct from physical-device endurance and
    // from the headless test runner's forced Skia draws. Reports name their host.
    internal async Task<DesktopEnduranceReport> RunEnduranceCampaign(int durationSeconds,Action? drawForHeadlessTest = null)
    {
        if (durationSeconds is < 30 or > 3600) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if ((!options.Automated && drawForHeadlessTest is null) || preferencesStore is not null || Controller.Connected)
            throw new InvalidOperationException("Endurance requires an isolated, disconnected automated window with settings disabled.");
        enduranceRunning = true;
        var clock = Stopwatch.StartNew(); var phases = new List<DesktopEndurancePhase>(); Exception? failure = null;
        try
        {
            for (int phase = 0; phase < 3; ++phase)
            {
                int protocol = phase == 1 ? 1 : 2;
                Control<ComboBox>("ProtocolInput").SelectedIndex = protocol == 2 ? 0 : 1;
                Control<ComboBox>("OutputInput").SelectedIndex = 0;
                Control<CheckBox>("MuteInput").IsChecked = true;
                await Connect();
                await WaitFor(() => Controller.Snapshot?.Spectrum is not null);
                if (!Controller.Connected || !Controller.Settings.Muted || Controller.Settings.AudioGainDb > -40)
                    throw new InvalidOperationException("Reconnect did not preserve conservative startup.");
                var held = Stopwatch.StartNew(); double nextControl = 0; int changes = 0;
                long lastFrames = 0,lastAudio = 0; double lastProgress = 0,lastAudioProgress = 0;
                using var process = Process.GetCurrentProcess(); long startMemory = process.WorkingSet64;
                double phaseSeconds = durationSeconds/3.0;
                while (held.Elapsed.TotalSeconds < phaseSeconds)
                {
                    drawForHeadlessTest?.Invoke();
                    var snapshot = Controller.Snapshot ?? throw new InvalidOperationException("Receive session disappeared.");
                    var r = snapshot.Receive; var o = snapshot.Output;
                    if (!Controller.Connected || Controller.Error is not null || o.Physical || !o.Muted || !o.Active || snapshot.NullRms != 0 ||
                        o.Underruns != 0 || o.DriverUnderruns != 0 || o.Rejected != 0 || o.NonfiniteSamples != 0 || o.ClippedSamples != 0 ||
                        r.MissingPackets != 0 || r.LatePackets != 0 || r.MalformedPackets != 0 || r.ForeignPackets != 0 ||
                        r.InputOverruns != 0 || r.AudioDropped != 0 || r.SocketErrors != 0 || r.DspErrors != 0)
                        throw new InvalidOperationException("Desktop endurance safety or receive/output counters failed.");
                    long frames = Control<SpectrumView>("SpectrumDisplay").FramesRendered;
                    if (frames > lastFrames) { lastFrames = frames; lastProgress = held.Elapsed.TotalSeconds; }
                    if (held.Elapsed.TotalSeconds-lastProgress > 10) throw new TimeoutException("No new spectrum was rendered for ten seconds.");
                    if (o.Rendered > lastAudio) { lastAudio = o.Rendered; lastAudioProgress = held.Elapsed.TotalSeconds; }
                    if (held.Elapsed.TotalSeconds-lastAudioProgress > 10) throw new TimeoutException("No output PCM advanced for ten seconds.");
                    if (held.Elapsed.TotalSeconds >= nextControl)
                    {
                        bool alternate = ++changes%2 == 0;
                        Control<TextBox>("FrequencyInput").Text = (alternate ? 14_201_000 : 14_198_500).ToString(CultureInfo.InvariantCulture);
                        Control<ComboBox>("ModeInput").SelectedIndex = alternate ? 1 : 0;
                        Control<TextBox>("LowInput").Text = alternate ? "400" : "300";
                        Control<TextBox>("HighInput").Text = alternate ? "2200" : "3000";
                        Control<ComboBox>("AgcInput").SelectedIndex = alternate ? 3 : 2;
                        await Apply();
                        if (Controller.Settings.FrequencyHz != (alternate ? 14_201_000 : 14_198_500)) throw new InvalidOperationException("Control change did not apply.");
                        Width = alternate ? 900 : 1400; Height = alternate ? 700 : 900;
                        Controller.Diagnostics.Event("window-resized");
                        nextControl = held.Elapsed.TotalSeconds+5;
                    }
                    await Task.Delay(100);
                }
                drawForHeadlessTest?.Invoke();
                if (DisplayedFrames < 10 || lastFrames < 10) throw new InvalidOperationException("Insufficient advancing rendered frames.");
                process.Refresh();
                phases.Add(new(protocol,held.Elapsed.TotalSeconds,DisplayedFrames,Control<SpectrumView>("SpectrumDisplay").FramesRendered,startMemory,process.WorkingSet64));
                Controller.Diagnostics.PublishDisplay(Control<SpectrumView>("SpectrumDisplay").Telemetry);
                await Controller.DisconnectAsync();
                if (Controller.Connected) throw new InvalidOperationException("Disconnect did not join the session.");
                var terminal = Controller.Diagnostics.Snapshot().Sessions[^1].LastObserved;
                if (terminal?.Output is not { Physical:false,Muted:true,Rejected:0,Underruns:0,DriverUnderruns:0,NonfiniteSamples:0,ClippedSamples:0,Fault:Thetis.Audio.PlaybackFault.None } ||
                    terminal.Receive is not { MissingPackets:0,LatePackets:0,MalformedPackets:0,ForeignPackets:0,SocketErrors:0,DspErrors:0,InputOverruns:0,AudioDropped:0 })
                    throw new InvalidOperationException("Final receive/output counters were not clean.");
            }
        }
        catch (Exception ex) { failure = ex; Controller.Diagnostics.Event("endurance-failed",ex); }
        finally
        {
            try { await Controller.DisposeAsync(); }
            catch (Exception ex) { failure ??= ex; Controller.Diagnostics.Event("endurance-cleanup-failed",ex); }
            enduranceRunning = false;
        }
        var diagnostics = Controller.Diagnostics.Snapshot();
        bool passed = failure is null && phases.Count == 3 && diagnostics.SessionsStarted == 3 && diagnostics.SessionsFailed == 0;
        return new(1,passed,durationSeconds,clock.Elapsed.TotalSeconds,failure?.GetType().Name,
            (drawForHeadlessTest is null ? "Native window" : "Headless UI with forced Skia draws")+
            "; P2/P1/P2, muted sample-driven no-device output. No physical audio/RF/TX or saved settings. Ten-second rendering-progress watchdog and zero receive/output error gates. CPU, memory and achieved cadence are measurements, not M5 performance qualification; target cadence remains 30 Hz.",
            phases.ToArray(),diagnostics);
    }
    private async Task RunEndurance()
    {
        int exit = 4;
        try
        {
            var report = await RunEnduranceCampaign(options.EnduranceSeconds);
            await AtomicJsonFile.WriteAsync(options.ReportPath!,report);
            Console.WriteLine(JsonSerializer.Serialize(new { report.Passed,report.ElapsedSeconds,report.RequestedConnectedSeconds,report.FailureType,report.Phases,report.Diagnostics.Resources }));
            if (report.Passed) exit = 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Desktop endurance failed: {ex.Message}"); }
        finally
        {
            try { await Controller.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"Desktop shutdown failed: {ex.Message}"); exit = 4; }
            mayClose = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown(exit);
            else Close();
        }
    }
}
