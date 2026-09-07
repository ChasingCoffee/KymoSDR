using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Thetis.Engine;
using Thetis.Headless;
using Thetis.Simulator;

// Isolated allocator experiment, not a hardware command or application allocator policy.
// Run each mode in a FRESH process so previous arena high-water marks cannot contaminate it.
if (args.Length is < 3 or > 4 || !Path.IsPathFullyQualified(args[0]) ||
    args[1] is not ("same" or "async" or "owner") || !int.TryParse(args[2], out int cycles) ||
    cycles is < 3 or > 20 || (args.Length == 4 && args[3] != "--release-reserved"))
{
    Console.Error.WriteLine("Usage: Thetis.MemoryProbe ABSOLUTE_NATIVE_DIR same|async|owner CYCLES(3..20) [--release-reserved]");
    return 2;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
using var owner = args[1] == "owner" ? new ProbeOwner() : null;
string directory = args[0], mode = args[1];
var timer = Stopwatch.StartNew();
var samples = new List<Snapshot>();
try
{
    DspRuntime.Initialize(directory);
    Sample(0, "baseline");
    for (int cycle = 1; cycle <= cycles; ++cycle)
    {
        timeout.Token.ThrowIfCancellationRequested();
        var simulator = G2Simulator.Open();
        P2ReceiveSession? session = null;
        int nativePort = 0;
        try
        {
            session = Call(() => P2ReceiveSession.Open(directory, new(simulator.BasePort), timeout.Token));
            nativePort = session.State.LocalPort;
            Sample(cycle, "open");
            ReceiveSelfTest.Measure(session, 1000, timeout.Token);
            ReceiveSelfTest.MeasureSpectrum(session, 14_199_000, 14_200_000, 1, timeout.Token);
            var state = session.State;
            if (state.DspErrors != 0 || state.SocketErrors != 0 || state.InputOverruns != 0 ||
                state.AudioDropped != 0 || state.MissingPackets != 0)
                throw new InvalidOperationException($"Receive errors in memory probe: {state}");
            if (mode != "same") await Task.Delay(25, timeout.Token);
            Sample(cycle, "before-close");
        }
        finally
        {
            try
            {
                if (session is not null) Call(() => { session.Dispose(); return 0; });
                var stopped = Stopwatch.StartNew();
                while (simulator.State.Running)
                {
                    if (stopped.Elapsed.TotalSeconds >= 2) throw new TimeoutException("No native STOP.");
                    Thread.Sleep(5);
                }
                if (simulator.State.WatchdogStops != 0 || simulator.State.Transmit.Packets != 0 || simulator.State.Transmit.Ptt)
                    throw new InvalidOperationException("Probe safety/shutdown invariant failed.");
                if (nativePort != 0)
                    using (var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, nativePort))) { }
            }
            finally { simulator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        Sample(cycle, "closed");
        if (mode != "same") await Task.Delay(25, timeout.Token);
    }
    if (args.Length == 4)
    {
        // Diagnostic-only control experiment, never used by the engine or normal tests.
        Sample(cycles, "before-release-reserved");
        NativeHeap.ReleaseReserved();
        Sample(cycles, "after-release-reserved");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, mode, cycles, elapsedMs = timer.ElapsedMilliseconds, samples }));
    return 0;
}
catch (Exception error)
{
    Console.WriteLine(JsonSerializer.Serialize(new { passed = false, mode, failure = error.ToString(), samples }));
    return error is OperationCanceledException ? 130 : 1;
}

T Call<T>(Func<T> action) => owner is null ? action() : owner.Invoke(action);
void Sample(int cycle, string phase)
{
    using var process = Process.GetCurrentProcess();
    var heap = NativeHeap.Read();
    var sample = new Snapshot(cycle, phase, Environment.CurrentManagedThreadId, NativeHeap.ThreadId(),
        process.WorkingSet64, GC.GetTotalMemory(false), heap.Used, heap.Reserved,
        owner?.ThreadId ?? 0);
    samples.Add(sample);
    Console.Error.WriteLine($"{mode} cycle={cycle} {phase}: tid={sample.OsThread} owner={sample.OwnerThread} " +
        $"RSS={sample.Rss / 1048576.0:F1} MiB, allocator-used={sample.HeapUsed / 1048576.0:F1} MiB, reserved={sample.HeapReserved / 1048576.0:F1} MiB");
}

internal sealed record Snapshot(int Cycle, string Phase, int ManagedThread, ulong OsThread, long Rss,
    long ManagedBytes, long? HeapUsed, long? HeapReserved, ulong OwnerThread);

internal static class NativeHeap
{
    internal static (long? Used, long? Reserved) Read()
    {
        if (OperatingSystem.IsMacOS())
        {
            malloc_zone_statistics(0, out var info);
            return (checked((long)info.Used), checked((long)info.Reserved));
        }
        if (OperatingSystem.IsLinux())
        {
            var info = mallinfo2();
            return (checked((long)(info.Used + info.Mapped)), checked((long)(info.Arena + info.Mapped)));
        }
        return (null, null); // No fabricated Windows allocator statistics.
    }
    internal static void ReleaseReserved()
    {
        if (OperatingSystem.IsMacOS()) _ = malloc_zone_pressure_relief(0, 0);
        else if (OperatingSystem.IsLinux()) _ = malloc_trim(0);
        else throw new PlatformNotSupportedException("No diagnostic allocator trim for this OS.");
    }
    internal static ulong ThreadId() => OperatingSystem.IsWindows() ? GetCurrentThreadId() :
        OperatingSystem.IsMacOS() ? (ulong)DarwinThread() : (ulong)LinuxThread();
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinInfo { public uint Blocks; public nuint Used, Peak, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxInfo { public nuint Arena, FreeChunks, FastChunks, Mappings, Mapped, Unused, FastFree, Used, Free, Top; }
    [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern void malloc_zone_statistics(nint zone, out DarwinInfo info);
    [DllImport("/usr/lib/libSystem.B.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint malloc_zone_pressure_relief(nint zone, nuint goal);
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern LinuxInfo mallinfo2();
    [DllImport("libc.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int malloc_trim(nuint pad);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_self", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint DarwinThread();
    [DllImport("libc.so.6", EntryPoint = "pthread_self", CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint LinuxThread();
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetCurrentThreadId();
}

// Probe-only candidate: no production lifecycle change until A/B evidence exists.
internal sealed class ProbeOwner : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread worker;
    internal ulong ThreadId { get; private set; }
    internal ProbeOwner()
    {
        worker = new(() => { ThreadId = NativeHeap.ThreadId(); foreach (var work in queue.GetConsumingEnumerable()) work(); })
            { IsBackground = true, Name = "Memory probe owner" };
        worker.Start();
    }
    internal T Invoke<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() => { try { done.SetResult(action()); } catch (Exception error) { done.SetException(error); } });
        return done.Task.GetAwaiter().GetResult();
    }
    public void Dispose() { queue.CompleteAdding(); worker.Join(); queue.Dispose(); }
}
