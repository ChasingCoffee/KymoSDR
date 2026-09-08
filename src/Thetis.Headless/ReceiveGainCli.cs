using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Simulator;

namespace Thetis.Headless;

internal static class ReceiveGainCli
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken token = default, Func<string, CancellationToken, Task<ReceiveGainResult>>? runner = null)
    {
        const string help = "Usage: Thetis.Headless receive-gain-selftest --native-dir ABSOLUTE_PATH\nTests gain, mute and AGC with owned P1/P2 loopback simulators. No hardware, TX or audio device.";
        if (args is [_, "--help"]) { output.WriteLine(help); return 0; }
        if (args.Length != 3 || args[1] != "--native-dir" || !Path.IsPathFullyQualified(args[2]))
        { error.WriteLine(help); return 2; }
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await (runner ?? ReceiveGainSelfTest.RunAsync)(args[2], token);
            output.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return result.Passed ? 0 : 4;
        }
        catch (OperationCanceledException) { error.WriteLine("Receive gain test cancelled; owners disposed."); return 130; }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or NotSupportedException)
        { error.WriteLine($"Cannot load native receive gain controls: {ex.Message}"); return 3; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or SocketException)
        { error.WriteLine($"Receive gain test failed: {ex.Message}"); return 4; }
    }
}

public sealed record GainSignalCheck(string Name, ReceiveGainState Gain, double Rms, double ExpectedRms);
public sealed record AgcStepCheck(ReceiveAgcMode Mode, ReceiveGainState Gain, double InitialWeakRms,
    double StrongRms, double EarlyRecoveryRms, double RecoveredWeakRms, double SilenceRms,
    double ReturnedStrongRms, double Peak, int ObservedDropWindowMilliseconds, int RecoveryWindowMilliseconds, ReceiveState Native,
    IReadOnlyList<double> WindowsRms);
public sealed record GainProtocolResult(int Protocol, IReadOnlyList<GainSignalCheck> GainChecks, IReadOnlyList<AgcStepCheck> AgcChecks);
public sealed record ReceiveGainResult(int SchemaVersion, bool Passed, bool LoopbackOnly, bool TransmitAllowed,
    long ElapsedMilliseconds, IReadOnlyList<GainProtocolResult> Protocols);

public static class ReceiveGainSelfTest
{
    // WDSP out_target: out_targ=1, n_tau=4; flat slope and unity panel gain.
    private static readonly double AgcRms = (1 - Math.Exp(-4)) * .9999 / Math.Sqrt(2);
    public static async Task<ReceiveGainResult> RunAsync(string directory, CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew(); var protocols = new List<GainProtocolResult>();
        foreach (int protocol in new[] {1,2}) protocols.Add(await RunProtocolAsync(directory, protocol, token));
        return new(1, true, true, false, clock.ElapsedMilliseconds, protocols);
    }
    public static async Task<GainProtocolResult> RunProtocolAsync(string directory, int protocol, CancellationToken token = default)
    {
        if (protocol is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(protocol));
        token.ThrowIfCancellationRequested(); var gains = new List<GainSignalCheck>(); var steps = new List<AgcStepCheck>();
        await using (var peer = Peer.Open(protocol))
        {
            var initial = new ReceiveGain(AudioGainDb: -6);
            using (var session = Open(directory, protocol, peer.Port, initial, token))
            {
                if (session.Gain.Settings != initial || session.Gain.Generation != 1) throw new InvalidOperationException("Startup gain was not applied.");
                Check("startup attenuation", initial, .25 / Math.Sqrt(2) * Math.Pow(10,-6/20.0));
                Check("unity/off", new(), .25/Math.Sqrt(2));
                Check("-20 dB/off", new(-20), .025/Math.Sqrt(2));
                Check("immediate mute/off", new(-20, true), 0);
                Check("unmute/off", new(-20), .025/Math.Sqrt(2));
                Check("fast AGC", new(AgcMode:ReceiveAgcMode.Fast), AgcRms);
                Check("mute/AGC", new(Muted:true,AgcMode:ReceiveAgcMode.Fast), 0);
                Check("unmute/AGC attenuation", new(-20,AgcMode:ReceiveAgcMode.Fast), AgcRms*.1);
                var unchanged = session.Gain; session.ConfigureGain(unchanged.Settings);
                if (session.Gain != unchanged) throw new InvalidOperationException("Idempotent gain changed state.");
                ReceiveSelfTest.MeasureSpectrum(session,14_199_000,14_200_000,1,token); // never scaled or muted by AF/AGC
                await Close(session,peer,token);

                void Check(string name, ReceiveGain settings, double expected)
                {
                    session.ConfigureGain(settings);
                    if (settings.Muted) AssertMutedPull(session);
                    var result = ReceiveControlsSelfTest.MeasureSettled(session,token);
                    if (expected == 0 ? result.Rms != 0 : Math.Abs(result.Rms-expected) > Math.Max(.00001,expected*.025))
                        throw new InvalidOperationException($"P{protocol} {name}: RMS {result.Rms:G9}, expected {expected:G9}.");
                    if (expected != 0 && Math.Abs(result.ToneHz-1000) > 20) throw new InvalidOperationException("Gain changed audio frequency.");
                    gains.Add(new(name,session.Gain,result.Rms,expected)); RequireClean(session.State);
                }
            }
            using (var muted = Open(directory,protocol,peer.Port,new(Muted:true,AgcMode:ReceiveAgcMode.Medium),token))
            {
                var result = ReceiveControlsSelfTest.MeasureSettled(muted,token);
                if (result.Rms != 0 || muted.Gain.Generation != 1) throw new InvalidOperationException("Muted startup leaked audio.");
                await Close(muted,peer,token);
            }
            using (var reset = Open(directory,protocol,peer.Port,new(),token))
            {
                if (reset.Gain.Settings != new ReceiveGain() || reset.Gain.Generation != 1) throw new InvalidOperationException("Reconnect retained hidden gain.");
                ReceiveSelfTest.Measure(reset,1000,token); await Close(reset,peer,token);
            }
        }
        await using (var weak = Peer.Open(protocol,.0001))
        {
            using var session = Open(directory,protocol,weak.Port,new(AgcMode:ReceiveAgcMode.Fast,AgcMaxGainDb:20),token);
            foreach (int top in new[] {20,60})
            {
                session.ConfigureGain(new(AgcMode:ReceiveAgcMode.Fast,AgcMaxGainDb:top));
                double expected = .0001 * Math.Pow(10,top/20.0) / Math.Sqrt(2);
                var result = ReceiveControlsSelfTest.MeasureSettled(session,token);
                if (Math.Abs(result.Rms-expected) > expected*.03) throw new InvalidOperationException($"AGC gain ceiling {top}: RMS {result.Rms:G9}, expected {expected:G9}.");
                gains.Add(new("below-knee maximum gain",session.Gain,result.Rms,expected)); RequireClean(session.State);
            }
            await Close(session,weak,token);
        }
        foreach (var mode in new[] {ReceiveAgcMode.Fast,ReceiveAgcMode.Medium,ReceiveAgcMode.Slow})
        {
            var profile = new SignalLevelProfile(new(1500,.25),new(3000,.005),new(7000,0),new(8000,.25));
            await using var peer = Peer.Open(protocol,.005,profile);
            using var session = Open(directory,protocol,peer.Port,new(AgcMode:mode),token);
            var result = MeasureSteps(session,mode,token); steps.Add(result);
            await Close(session,peer,token);
        }
        // Presets need not have monotonic total recovery times: WDSP also has a
        // fast-decay/pop path. Compare the early response and bounded recovery.
        if (steps[0].EarlyRecoveryRms <= steps[1].EarlyRecoveryRms*2 || steps[1].EarlyRecoveryRms <= steps[2].EarlyRecoveryRms*1.2)
            throw new InvalidOperationException($"AGC early responses did not separate: {JsonSerializer.Serialize(steps.Select(s => new {s.Mode,s.EarlyRecoveryRms,s.RecoveryWindowMilliseconds,windows = s.WindowsRms.Skip(28).Take(25)}))}");
        return new(protocol,gains,steps);
    }
    private static ReceiveSession Open(string directory,int protocol,int port,ReceiveGain gain,CancellationToken token) => protocol == 1
        ? P1ReceiveSession.Open(directory,new(port,Gain:gain),token) : P2ReceiveSession.Open(directory,new(port,Gain:gain),token);
    private static void AssertMutedPull(ReceiveSession session)
    {
        double[] samples = new double[4096]; int n = session.ReadAudio(samples);
        for (int i = 0; i < 2*n; ++i) if (samples[i] != 0) throw new InvalidOperationException("Mute leaked queued/tail audio.");
    }
    private static AgcStepCheck MeasureSteps(ReceiveSession session,ReceiveAgcMode mode,CancellationToken token)
    {
        const int windowFrames = 4800, windowCount = 95;
        double[] rms = new double[windowCount], buffer = new double[4096];
        int frames = 0, window = 0; double energy = 0, peak = 0;
        var clock = Stopwatch.StartNew();
        while (window < windowCount)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed.TotalSeconds > 25) throw new TimeoutException("AGC step stream stalled.");
            int count = session.ReadAudio(buffer);
            for (int i = 0; i < count && window < windowCount; ++i)
            {
                double left = buffer[2*i], right = buffer[2*i+1];
                if (!double.IsFinite(left) || !double.IsFinite(right)) throw new InvalidDataException("Non-finite AGC transition.");
                peak = Math.Max(peak,Math.Max(Math.Abs(left),Math.Abs(right))); energy += left*left;
                if (++frames == windowFrames) { rms[window++] = Math.Sqrt(energy/frames); frames = 0; energy = 0; }
            }
            RequireClean(session.State);
            if (count == 0) Thread.Sleep(2);
        }
        double Mean(int first,int end) => rms.Skip(first).Take(end-first).Average();
        var response = AnalyzeRecovery(rms);
        var result = new AgcStepCheck(mode,session.Gain,Mean(6,13),Mean(20,28),response.EarlyRms,Mean(60,68),Mean(75,79),Mean(85,93),peak,
            response.DropIndex*100,response.RecoveryMilliseconds,session.State,rms);
        foreach (double value in new[] {result.InitialWeakRms,result.StrongRms,result.RecoveredWeakRms,result.ReturnedStrongRms})
            if (Math.Abs(value-AgcRms) > .025) throw new InvalidOperationException($"AGC steady/step check failed: {result}");
        if (result.SilenceRms > 1e-6 || peak > 1.5)
            throw new InvalidOperationException($"AGC silence, peak or bounded recovery failed: {result}");
        return result;
    }
    internal static (int DropIndex, int RecoveryMilliseconds, double EarlyRms) AnalyzeRecovery(IReadOnlyList<double> rms)
    {
        if (rms.Count != 95 || rms.Any(x => !double.IsFinite(x) || x < 0))
            throw new InvalidDataException("Expected 95 finite, nonnegative 100 ms audio RMS windows.");
        // The source steps at 3 s, but ChannelMaster/WDSP queue audio. Locate the
        // observed drop within a bounded latency allowance; never count the old
        // strong plateau as recovery. These are windowed observations, not AGC taus.
        int drop = -1;
        for (int i = 30; i < 40; ++i) if (rms[i] < AgcRms*.6) { drop = i; break; }
        if (drop < 0) throw new InvalidOperationException("AGC strong-to-weak audio drop was not observed within 1 s of the source step.");
        int recovery = -1;
        for (int i = drop+1; i <= drop+30; ++i)
            if (rms[i] >= AgcRms*.9 && rms[i+1] >= AgcRms*.9 && rms[i+2] >= AgcRms*.9)
            { recovery = i; break; }
        if (recovery < 0) throw new InvalidOperationException("AGC did not sustain 90% recovery within 3 s of the observed drop.");
        return (drop,(recovery-drop)*100,(rms[drop+1]+rms[drop+2])/2);
    }
    private static void RequireClean(ReceiveState s)
    {
        if (s.SocketErrors != 0 || s.DspErrors != 0 || s.InputOverruns != 0 || s.AudioDropped != 0 || s.MissingPackets != 0)
            throw new InvalidOperationException($"Receive gain diagnostics failed: {s}");
    }
    private static async Task Close(ReceiveSession session,Peer peer,CancellationToken token)
    {
        RequireClean(session.State); int port = session.State.LocalPort; session.Dispose();
        await P1ReceiveSelfTest.WaitUntil(() => !peer.Running,token);
        peer.CheckSafety(); using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback,port));
    }
    private sealed class Peer : IAsyncDisposable
    {
        private readonly P1Simulator? p1;
        private readonly G2Simulator? p2;
        private Peer(P1Simulator value) { p1 = value; }
        private Peer(G2Simulator value) { p2 = value; }
        internal int Port => p1?.Port ?? p2!.BasePort;
        internal bool Running => p1?.State.Running ?? p2!.State.Running;
        internal static Peer Open(int protocol,double amplitude = .25,SignalLevelProfile? profile = null) => protocol == 1
            ? new(P1Simulator.Open(new(Amplitude:amplitude,SignalLevels:profile)))
            : new(G2Simulator.Open(new(BasePort:0,Amplitude:amplitude,SignalLevels:profile)));
        internal void CheckSafety()
        {
            if (p1 is not null)
            { var s = p1.State; if (s.Running || s.UnsafeRequests != 0 || s.SocketErrors != 0 || s.WatchdogStops != 0) throw new InvalidOperationException("P1 gain fixture safety check failed."); }
            else
            { var s = p2!.State; if (s.Running || s.UnsafeRequests != 0 || s.SocketErrors != 0 || s.WatchdogStops != 0 || s.Transmit.Ptt || s.Transmit.Packets != 0) throw new InvalidOperationException("P2 gain fixture safety check failed."); }
        }
        public ValueTask DisposeAsync() => p1?.DisposeAsync() ?? p2!.DisposeAsync();
    }
}
