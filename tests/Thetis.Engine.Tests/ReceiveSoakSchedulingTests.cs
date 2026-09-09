using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Engine.Tests;

[TestClass,DoNotParallelize]
public sealed class ReceiveSoakSchedulingTests
{
    private static string Native()
    {
        string? native = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(native)) Assert.Inconclusive("Native libraries required; loopback/no-device only.");
        return native;
    }
    [TestMethod,TestCategory("Native")]
    public async Task DelayedCallerContinuationCannotStarveTheAudioReader()
    {
        string native = Native();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // Only this test's caller scheduler is constrained. Never change global
        // ThreadPool limits, CPU affinity, driver settings or the simulator clock.
        var schedulers = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default,1);
        var caller = new TaskFactory(schedulers.ExclusiveScheduler);
        Task? obstruction = null; int injected = 0;
        try
        {
            var campaign = caller.StartNew(() => ReceiveSoak.RunAsync(native,new(10,1),p =>
            {
                if (p.Phase == "packet-loss" && Interlocked.CompareExchange(ref injected,1,0) == 0)
                    obstruction = caller.StartNew(() => Thread.Sleep(1000));
            },timeout.Token)).Unwrap();
            var result = await campaign.WaitAsync(TimeSpan.FromSeconds(100));
            Assert.AreEqual(1,injected); Assert.IsNotNull(obstruction); await obstruction;
            Assert.IsTrue(result.Passed,JsonSerializer.Serialize(new { result.Failure,Phases = result.Phases.Select(p =>
                new { p.Name,p.Passed,p.Failure,p.Native,p.NativeDisposed,p.StopObserved,p.PortRebound,p.ReaderTiming }) }));
            foreach (var phase in result.Phases)
            {
                Assert.IsNotNull(phase.ReaderTiming);
                Assert.IsFalse(phase.ReaderTiming.UsesThreadPool);
                Assert.IsTrue(phase.ReaderTiming.Reads > 0 && phase.ReaderTiming.Polls >= phase.ReaderTiming.Reads);
            }
            var loss = result.Phases.Single(p => p.Name == "packet-loss");
            Assert.IsTrue(loss.Native!.MissingPackets > 0);
            Assert.AreEqual(0L,loss.Native.AudioDropped);
            Assert.IsTrue(result.Phases.Single(p => p.Name == "slow-reader").Native!.AudioDropped > 0,
                "Intentional reader starvation must still fail the normal zero-drop gate.");
            Assert.AreEqual(1,result.ReconnectsCompleted);
        }
        finally
        {
            timeout.Cancel(); schedulers.Complete();
            await schedulers.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod,TestCategory("Native"),DataRow(false),DataRow(true)]
    public async Task BlockingReaderOrTerminalReporterStillFailsAndCleansUp(bool terminal)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        int injected = 0;
        var result = await ReceiveSoak.RunAsync(Native(),new(10,1),p =>
        {
            if (p.Phase == "packet-loss" && (!terminal || p.ElapsedSeconds >= 3) &&
                Interlocked.CompareExchange(ref injected,1,0) == 0) Thread.Sleep(1000);
        },timeout.Token);
        Assert.AreEqual(1,injected); Assert.IsFalse(result.Passed || result.Cancelled);
        StringAssert.Contains(result.Failure!,"Unplanned audio reader drops.");
        var loss = result.Phases.Single(p => p.Name == "packet-loss");
        Assert.IsTrue(loss.Native!.AudioDropped > 0);
        Assert.IsTrue(loss.ReaderTiming!.MaxProgressMilliseconds >= 900);
        Assert.IsFalse(loss.ReaderTiming.UsesThreadPool);
        Assert.IsTrue(loss.NativeDisposed && loss.StopObserved && loss.PortRebound);
        Assert.AreEqual(2,result.Phases.Count); Assert.AreEqual(0,result.ReconnectsCompleted);
    }
}
