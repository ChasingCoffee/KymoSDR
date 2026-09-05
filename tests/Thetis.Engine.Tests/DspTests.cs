using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;

[assembly: DoNotParallelize]
namespace Thetis.Engine.Tests;

[TestClass]
public sealed class DspTests
{
    private static string NativeDirectory()
    {
        string? directory = Environment.GetEnvironmentVariable("THETIS_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            Assert.Inconclusive("Native tests require THETIS_NATIVE_DIR pointing to the CMake stage/Release directory.");
        return directory;
    }

    [TestMethod]
    public void RelativeNativePathsAreRejectedBeforeLoading() =>
        Assert.ThrowsExactly<ArgumentException>(() => DspRuntime.Initialize("relative/native"));

    [TestMethod]
    [TestCategory("Native")]
    public void NativeAbiMatchesManagedContract()
    {
        DspAbiInfo abi = DspRuntime.Initialize(NativeDirectory());
        Assert.AreEqual(200, abi.WdspVersion);
        Assert.AreEqual(IntPtr.Size, abi.PointerBytes);
        Assert.AreEqual(4, abi.LongBytes);
        Assert.AreEqual(8, abi.AnalyzerInputBytes);
        Assert.AreEqual(4, abi.AnalyzerOutputBytes);
        Assert.AreEqual(480, abi.NoiseFrameSize);
        Assert.AreEqual(abi, DspRuntime.Initialize(NativeDirectory()));
    }

    [TestMethod]
    [TestCategory("Native")]
    public void SyntheticSignalsPassExplicitTolerances()
    {
        DspSelfTestResult result = DspDiagnostics.Run(NativeDirectory());
        foreach (var check in result.Checks)
            Assert.IsTrue(check.Passed, $"{check.Name}: {check.Measured:G17}; expected {check.Requirement}");
        Assert.AreEqual(11, result.Checks.Count);
        Assert.IsTrue(result.Passed);
    }

    [TestMethod]
    [TestCategory("Native")]
    public void RepeatedAnalyzerAndReceiverLifecycle()
    {
        DspRuntime.Initialize(NativeDirectory());
        lock (DspRuntime.Gate)
        {
            NativeMethods.ThetisWdspSetPlanningTimeLimit(0);
            try
            {
                for (int i = 0; i < 20; ++i)
                {
                    var spectrum = DspDiagnostics.MeasureSpectrum(CancellationToken.None);
                    Assert.AreEqual(14.2, spectrum.Reference);
                    var receiver = DspDiagnostics.MeasureReceiver(CancellationToken.None);
                    Assert.IsTrue(double.IsFinite(receiver.Pass) && receiver.Pass > 0.001);
                }
            }
            finally { NativeMethods.ThetisWdspSetPlanningTimeLimit(-1); }
        }
    }
}
