using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Thetis.Engine;

/// <summary>
/// One process-lifetime allocation/free thread for the process-owned native topology.
/// Hot audio/spectrum reads stay on their callers. Dispatch before taking DspRuntime.Gate.
/// </summary>
internal static class NativeLifecycle
{
    private static readonly BlockingCollection<Action> Pending = new(64);
    private static readonly Thread Worker;

    static NativeLifecycle()
    {
        Worker = new(Run) { IsBackground = true, Name = "Thetis native lifecycle" };
        // A permanent worker must not retain its first caller's AsyncLocal/request graph.
        if (ExecutionContext.IsFlowSuppressed()) Worker.Start();
        else
        {
            using (ExecutionContext.SuppressFlow()) Worker.Start();
        }
    }

    internal static T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        // Startup cancellation can dispose an already-open handle on this thread.
        if (Thread.CurrentThread == Worker) return action();
        if (Monitor.IsEntered(DspRuntime.Gate))
            throw new InvalidOperationException("Dispatch native lifecycle work before taking DspRuntime.Gate.");
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending.Add(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception error) { completion.SetException(error); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    private static void Run()
    {
        // Deliberately lives as long as the loaded native library. Never stop it while
        // a SafeHandle finalizer could still need to synchronously close native workers.
        while (true) ExecuteNext();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExecuteNext()
    {
        // Keep the last delegate/result out of the blocked consumer's stack frame.
        // Otherwise an abandoned session could stay rooted until the next invocation.
        Pending.Take()();
    }
}
