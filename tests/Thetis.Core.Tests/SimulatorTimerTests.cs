using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SimulatorTimerTests
{
    [TestMethod]
    public void NonWindowsNeverCallsTheNativeTimerApi()
    {
        using var scope = SimulatorTimerResolution.Acquire(false,
            _ => throw new AssertFailedException(), _ => throw new AssertFailedException());
    }

    [TestMethod]
    public void SuccessfulTimerRequestIsBalancedOnceIncludingExceptionalExit()
    {
        int begins = 0, ends = 0;
        var scope = SimulatorTimerResolution.Acquire(true,
            period => { Assert.AreEqual(1u, period); ++begins; return 0; },
            period => { Assert.AreEqual(1u, period); ++ends; return 0; });
        Assert.ThrowsExactly<IOException>(() => { using (scope) throw new IOException("worker failure"); });
        scope.Dispose();
        Assert.AreEqual(1, begins); Assert.AreEqual(1, ends);
    }

    [TestMethod]
    public void FailedAcquisitionDoesNotReleaseAnUnownedTimerRequest()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => SimulatorTimerResolution.Acquire(true,
            _ => 97, _ => throw new AssertFailedException()));
    }

    [TestMethod]
    public void ReleaseFailureIsReportedWithoutDoubleRelease()
    {
        int ends = 0;
        var scope = SimulatorTimerResolution.Acquire(true, _ => 0, _ => { ++ends; return 97; });
        Assert.ThrowsExactly<InvalidOperationException>(() => scope.Dispose());
        scope.Dispose(); Assert.AreEqual(1, ends);
    }
}
