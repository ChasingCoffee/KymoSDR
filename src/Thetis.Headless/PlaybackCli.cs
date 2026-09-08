using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Thetis.Audio;
using Thetis.Engine;
using Thetis.Preview;

namespace Thetis.Headless;

internal static class PlaybackCli
{
    internal static async Task<int> RunAsync(string[] args,TextWriter output,TextWriter error,CancellationToken token = default)
    {
        const string help = "Commands: playback-selftest --native-dir ABSOLUTE_PATH | audio-devices --native-dir ABSOLUTE_PATH | playback-listen --native-dir ABSOLUTE_PATH --device INDEX [--seconds 1..30] [--unmute]\nSelf-test is loopback/no-device. Listen opens the explicitly selected audio output, starts muted at -40 dB, and only unmutes with --unmute. No microphone, LAN radio or TX.";
        if (args is [_,"--help"]) { output.WriteLine(help); return 0; }
        int device = -1, seconds = 10; bool unmute = false;
        if (args.Length < 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2])) { error.WriteLine(help); return 2; }
        bool listen = args[0] == "playback-listen";
        if (!listen && (args.Length != 3 || args[0] is not ("playback-selftest" or "audio-devices"))) { error.WriteLine(help); return 2; }
        var seen = new HashSet<string>();
        for (int i = 3; i < args.Length; ++i)
        {
            string key = args[i];
            if (!seen.Add(key)) { error.WriteLine(help); return 2; }
            if (key == "--unmute") unmute = true;
            else if (key is "--device" or "--seconds" && ++i < args.Length && int.TryParse(args[i],NumberStyles.Integer,CultureInfo.InvariantCulture,out int number))
            { if (key == "--device") device = number; else seconds = number; }
            else { error.WriteLine(help); return 2; }
        }
        if (listen && (device < 0 || seconds is < 1 or > 30)) { error.WriteLine(help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            object result;
            if (args[0] == "audio-devices") result = await Task.Run(() => PlaybackOutput.EnumerateDevices(args[2]),token);
            else if (!listen) result = await PlaybackSelfTest.RunAsync(args[2],token);
            else
            {
                var devices = await Task.Run(() => PlaybackOutput.EnumerateDevices(args[2]),token);
                var selected = devices.SingleOrDefault(d => d.Index == device) ?? throw new ArgumentException("Selected device is unavailable; run audio-devices again.");
                await using var controller = new PreviewController();
                await controller.ConnectAsync(args[2],device:selected,token:token);
                await Task.Delay(1000,token);
                if (unmute) await controller.ApplyAsync(controller.Settings with { Muted = false },token);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    token.ThrowIfCancellationRequested();
                    if (controller.Error is { } failure) throw new IOException("Audio output failed.",failure);
                    await Task.Delay(50,token);
                }
                result = new { loopbackOnly = true, physicalAudio = true, unmuted = unmute, device = selected,
                    snapshot = controller.Snapshot, note = "Device opened; audible quality still requires human observation." };
            }
            output.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return 0;
        }
        catch (OperationCanceledException) { error.WriteLine("Playback cancelled; output and simulator owners disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Playback native load failed: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException or System.Net.Sockets.SocketException)
        { error.WriteLine($"Playback failed: {ex.Message}"); return 4; }
    }
}
public sealed record PlaybackCheck(int Protocol,double Rms,double ToneHz,long SpectrumFrames,PlaybackSnapshot Snapshot);
public sealed record PlaybackResult(bool Passed,bool LoopbackOnly,bool PhysicalAudio,long ElapsedMilliseconds,IReadOnlyList<PlaybackCheck> Checks);
public static class PlaybackSelfTest
{
    public static async Task<PlaybackResult> RunAsync(string directory,CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew(); List<PlaybackCheck> checks = [];
        foreach (int protocol in new[] {1,2})
        {
            await using var controller = new PreviewController(); await controller.ConnectAsync(directory,protocol,token:token);
            await WaitFor(controller,s => s.Output.Rendered > 48000 && s.Spectrum is not null,token);
            if (controller.Snapshot is not { } initial || !initial.Output.Muted || initial.Output.Physical || initial.NullRms != 0)
                throw new InvalidOperationException("Playback must start muted with no device.");
            await controller.ApplyAsync(new(AudioGainDb:-20,Muted:false,AgcMode:ReceiveAgcMode.Off),token);
            long start = controller.Snapshot.Output.Rendered;
            await WaitFor(controller,s => s.Output.Rendered > start+96000,token);
            var measured = controller.Snapshot!;
            CheckClean(measured);
            if (Math.Abs(measured.NullRms-.025/Math.Sqrt(2)) > .001 || Math.Abs(measured.NullToneHz-1000) > 10)
                throw new InvalidOperationException($"P{protocol} playback signal mismatch: {measured.NullRms:G9} RMS, {measured.NullToneHz} Hz.");
            checks.Add(new(protocol,measured.NullRms,measured.NullToneHz,measured.Spectrum!.Sequence,measured));
            await controller.ApplyAsync(controller.Settings with { Muted = true },token);
            await WaitFor(controller,s => s.Output.Rendered > measured.Output.Rendered+48000 && s.NullRms == 0,token);
            await controller.DisconnectAsync();
            await controller.ConnectAsync(directory,protocol,token:token);
            await WaitFor(controller,s => s.Output.Rendered > 48000,token);
            if (!controller.Settings.Muted || controller.Settings.AudioGainDb > -40 || controller.Snapshot!.NullRms != 0)
                throw new InvalidOperationException("Reconnect restored unsafe playback state.");
        }
        return new(true,true,false,clock.ElapsedMilliseconds,checks);
    }
    internal static void CheckClean(PlaybackSnapshot s)
    {
        if (s.Receive.SocketErrors != 0 || s.Receive.DspErrors != 0 || s.Receive.MissingPackets != 0 || s.Receive.InputOverruns != 0 || s.Receive.AudioDropped != 0 ||
            s.Output.Rejected != 0 || s.Output.Underruns != 0 || s.Output.DriverUnderruns != 0 || s.Output.NonfiniteSamples != 0 || s.Output.ClippedSamples != 0)
            throw new InvalidOperationException($"Playback diagnostics failed: {s.Receive}; {s.Output}");
    }
    private static async Task WaitFor(PreviewController controller,Func<PlaybackSnapshot,bool> ready,CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (controller.Snapshot is not { } snapshot || !ready(snapshot))
        {
            token.ThrowIfCancellationRequested();
            if (controller.Error is { } error) throw new IOException("Playback pump stopped.",error);
            if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("Playback/spectrum did not advance.");
            await Task.Delay(10,token);
        }
    }
}
