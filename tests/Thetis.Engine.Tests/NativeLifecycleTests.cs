using System.Runtime.CompilerServices;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Thetis.Engine.Tests;

[TestClass]
public sealed class NativeLifecycleTests
{
    private static readonly AsyncLocal<object?> CallerContext = new();

    [TestMethod]
    public async Task DifferentCallersUseOneBackgroundThreadAndReentrantCallsStayInline()
    {
        var callers = Enumerable.Range(0, 12).Select(_ => Task.Factory.StartNew(() =>
        {
            int caller = Environment.CurrentManagedThreadId;
            return NativeLifecycle.Invoke(() => (caller, owner: Environment.CurrentManagedThreadId,
                nested: NativeLifecycle.Invoke(() => Environment.CurrentManagedThreadId),
                background: Thread.CurrentThread.IsBackground));
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var results = await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, results.Select(r => r.owner).Distinct().Count());
        foreach (var r in results)
        {
            Assert.AreNotEqual(r.caller, r.owner);
            Assert.AreEqual(r.owner, r.nested);
            Assert.IsTrue(r.background);
        }
    }

    [TestMethod]
    public void ExceptionsPropagateWithoutKillingWorkerAndGateInversionIsRejected()
    {
        var injected = new IOException("injected lifecycle failure");
        Assert.AreSame(injected, Assert.ThrowsExactly<IOException>(() => NativeLifecycle.Invoke<int>(() => throw injected)));
        Assert.AreEqual(42, NativeLifecycle.Invoke(() => 42));
        lock (DspRuntime.Gate)
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeLifecycle.Invoke(() => 0));
        Assert.AreEqual(42, NativeLifecycle.Invoke(() => { lock (DspRuntime.Gate) return 42; }));
    }

    [TestMethod]
    public void CallerExecutionContextAndLastCompletedWorkAreNotRetained()
    {
        var garbage = InvokeWithCallerObjects();
        var wait = Stopwatch.StartNew();
        do
        {
            // The waiting caller can wake just before the worker returns from SetResult.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (!garbage.Context.IsAlive && !garbage.Payload.IsAlive) break;
            Thread.Sleep(10);
        } while (wait.Elapsed < TimeSpan.FromSeconds(2));
        Assert.IsFalse(garbage.Context.IsAlive, "The worker retained the caller's execution context.");
        Assert.IsFalse(garbage.Payload.IsAlive, "The idle worker retained its last completed delegate.");
        using (ExecutionContext.SuppressFlow())
            Assert.IsNull(NativeLifecycle.Invoke(() => CallerContext.Value));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, WeakReference Payload) InvokeWithCallerObjects()
    {
        object context = new(), payload = new();
        CallerContext.Value = context;
        try
        {
            NativeLifecycle.Invoke(() =>
            {
                Assert.IsNull(CallerContext.Value);
                GC.KeepAlive(payload);
                return 0;
            });
            return (new(context), new(payload));
        }
        finally { CallerContext.Value = null; }
    }
}
