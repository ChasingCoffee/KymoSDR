using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Thetis.Audio;
using Thetis.Engine;

namespace Thetis.Preview;

public sealed record DisplayTelemetry(long AcceptedFrames = 0,long RenderedFrames = 0,long SourceFramesSkipped = 0,
    double Width = 0,double Height = 0);
public sealed record ProcessTelemetry(long WorkingSetBytes,long ManagedHeapBytes,long TotalAllocatedBytes,double CpuSeconds)
{
    internal static ProcessTelemetry Read()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.WorkingSet64,GC.GetTotalMemory(false),GC.GetTotalAllocatedBytes(false),process.TotalProcessorTime.TotalSeconds);
    }
}
public sealed record ReceiveCounters(int InputRate,long IqPackets,long IqSamples,long MissingPackets,long LatePackets,
    long MalformedPackets,long ForeignPackets,long SocketErrors,long DspErrors,long InputOverruns,long AudioDropped,long AudioProduced)
{
    internal static ReceiveCounters From(ReceiveState s) => new(s.InputRate,s.IqPackets,s.IqSamples,s.MissingPackets,s.LatePackets,
        s.MalformedPackets,s.ForeignPackets,s.SocketErrors,s.DspErrors,s.InputOverruns,s.AudioDropped,s.AudioProduced);
}
public sealed record SimulatorClockDiagnostics(long IqPacingResyncs,long IqPacingLostNanoseconds);
public sealed record DiagnosticSample(double ElapsedSeconds,long SessionId,ProcessTelemetry Process,double? CpuPercentOneCore,
    double? DisplayFramesPerSecond,double? RenderFramesPerSecond,double? SampleGapSeconds,DisplayTelemetry Display,
    ReceiveCounters? Receive,PlaybackState? Output,SimulatorClockDiagnostics? SimulatorClock);
public sealed record DiagnosticEvent(double ElapsedSeconds,long SessionId,string Kind,string? ErrorType = null,PreviewSettings? Controls = null);
public sealed record DiagnosticSession(long Id,int Protocol,double StartedSeconds,double? EndedSeconds,
    PreviewSettings InitialControls,DiagnosticSample? LastObserved,string? ErrorType);
public sealed record ResourceSummary(ProcessTelemetry? First,ProcessTelemetry? Last,long PeakWorkingSetBytes,
    double PeakCpuPercentOneCore,double MaxSampleGapSeconds,long SamplesRecorded);
public sealed record DiagnosticReport(int SchemaVersion,DateTimeOffset CreatedUtc,string SourceVersion,string Runtime,
    string OperatingSystem,string Architecture,int LogicalProcessors,bool LoopbackOnly,string Qualification,
    int SampleCapacity,int EventCapacity,int SessionCapacity,long EvictedSamples,long EvictedEvents,long EvictedSessions,
    long SessionsStarted,long SessionsFailed,ResourceSummary Resources,DiagnosticSample[] Samples,
    DiagnosticEvent[] Events,DiagnosticSession[] Sessions);

/// <summary>Bounded, process-local observations. No waveform data, device names, addresses or file paths.
/// Recording and JSON export never execute on the PCM pump or native callback.</summary>
public sealed class PreviewDiagnostics
{
    public const int SampleCapacity = 3600,EventCapacity = 256,SessionCapacity = 32;
    private readonly object gate = new();
    private readonly Queue<DiagnosticSample> samples = new();
    private readonly Queue<DiagnosticEvent> events = new();
    private readonly List<DiagnosticSession> sessions = new();
    private readonly Func<double> seconds;
    private readonly Func<ProcessTelemetry> readProcess;
    private DisplayTelemetry display = new();
    private DiagnosticSample? previous;
    private ProcessTelemetry? first,last;
    private long nextId,activeId,failed,evictedSamples,evictedEvents,evictedSessions,sampleCount,peakMemory;
    private double peakCpu,maxGap;
    public PreviewDiagnostics() : this(Stopwatch.StartNew(),ProcessTelemetry.Read) { }
    private PreviewDiagnostics(Stopwatch clock,Func<ProcessTelemetry> process) : this(() => clock.Elapsed.TotalSeconds,process) { }
    internal PreviewDiagnostics(Func<double> seconds,Func<ProcessTelemetry> process)
    { this.seconds = seconds; readProcess = process; }
    public void PublishDisplay(DisplayTelemetry value) => Volatile.Write(ref display,value);
    public long Begin(int protocol,PreviewSettings controls)
    {
        lock (gate)
        {
            if (activeId != 0) throw new InvalidOperationException("A diagnostic session is already active.");
            activeId = ++nextId;
            if (sessions.Count == SessionCapacity) { sessions.RemoveAt(0); ++evictedSessions; }
            sessions.Add(new(activeId,protocol,seconds(),null,controls,null,null));
            AddEvent(new(seconds(),activeId,"connected",Controls:controls)); return activeId;
        }
    }
    public void Record(long id,PlaybackSnapshot? snapshot,SimulatorClockDiagnostics? simulator = null)
    {
        lock (gate)
        {
            if (id == 0 || id != activeId) return; // late sampling cannot cross a reconnect
            double now = seconds(); var process = readProcess(); var drawing = Volatile.Read(ref display);
            double? gap = previous is null ? null : Math.Max(0,now-previous.ElapsedSeconds);
            // Adjacent terminal snapshots may be microseconds apart; OS CPU
            // counters are not precise enough to derive rates over that interval.
            double? cpu = gap >= .25 ? Math.Max(0,100*(process.CpuSeconds-previous!.Process.CpuSeconds)/gap.Value) : null;
            double? fps = gap >= .25 ? Math.Max(0,(drawing.AcceptedFrames-previous!.Display.AcceptedFrames)/gap.Value) : null;
            double? rendered = gap >= .25 ? Math.Max(0,(drawing.RenderedFrames-previous!.Display.RenderedFrames)/gap.Value) : null;
            var sample = new DiagnosticSample(now,id,process,cpu,fps,rendered,gap,drawing,
                snapshot is null ? null : ReceiveCounters.From(snapshot.Receive),snapshot?.Output,
                simulator ?? (previous?.SessionId == id ? previous.SimulatorClock : null));
            if (samples.Count == SampleCapacity) { samples.Dequeue(); ++evictedSamples; }
            samples.Enqueue(sample); previous = sample; first ??= process; last = process; ++sampleCount;
            peakMemory = Math.Max(peakMemory,process.WorkingSetBytes); peakCpu = Math.Max(peakCpu,cpu ?? 0); maxGap = Math.Max(maxGap,gap ?? 0);
            sessions[^1] = sessions[^1] with { LastObserved = sample };
        }
    }
    public void End(long id,Exception? error)
    {
        lock (gate)
        {
            if (id == 0 || id != activeId) return;
            if (error is not null) ++failed;
            sessions[^1] = sessions[^1] with { EndedSeconds = seconds(),ErrorType = error?.GetType().Name };
            AddEvent(new(seconds(),id,error is null ? "disconnected" : "session-failed",error?.GetType().Name)); activeId = 0;
        }
    }
    public void Event(string kind,Exception? error = null,PreviewSettings? controls = null)
    { lock (gate) AddEvent(new(seconds(),activeId,kind,error?.GetType().Name,controls)); }
    private void AddEvent(DiagnosticEvent value)
    { if (events.Count == EventCapacity) { events.Dequeue(); ++evictedEvents; } events.Enqueue(value); }
    public DiagnosticReport Snapshot()
    {
        lock (gate) return new(1,DateTimeOffset.UtcNow,
            typeof(PreviewDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            RuntimeInformation.FrameworkDescription,RuntimeInformation.OSDescription,RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,true,
            "Simulator observations only. Silent-monitor timing is sample-driven, not physical audio endurance. CPU 100% equals one logical core; process memory includes the full application. Samples target 1 second; gaps and bounded-history evictions are explicit. No RF/TX, waveform, device names, network addresses or local paths are exported.",
            SampleCapacity,EventCapacity,SessionCapacity,evictedSamples,evictedEvents,evictedSessions,nextId,failed,
            new(first,last,peakMemory,peakCpu,maxGap,sampleCount),samples.ToArray(),events.ToArray(),sessions.ToArray());
    }
    public Task ExportAsync(string path,CancellationToken token = default) => AtomicJsonFile.WriteAsync(path,Snapshot(),token);
}
