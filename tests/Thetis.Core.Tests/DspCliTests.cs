using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class DspCliTests
{
    [TestMethod]
    public void HelpDoesNotLoadNativeCode()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, DspCli.Run(["dsp-selftest", "--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "Offline only");
    }

    [TestMethod]
    public void InvalidOptionsDoNotInvokeNativeRunner()
    {
        Assert.AreEqual(2, DspCli.Run(["dsp-selftest"], TextWriter.Null, TextWriter.Null));
        Assert.AreEqual(2, DspCli.Run(["dsp-selftest", "--native-dir", "relative"], TextWriter.Null, TextWriter.Null));
    }

    [TestMethod]
    public void ResultAndExitCodeReflectNativeChecks()
    {
        string path = Path.GetTempPath();
        var abi = new DspAbiInfo(path, 200, 8, 4, 8, 4, 480);
        using var output = new StringWriter();
        Assert.AreEqual(1, DspCli.Run(["dsp-selftest", "--native-dir", path], output, TextWriter.Null,
            runner: (_, _) => new(1, abi, false, 12, [new("fixture", 2, "< 1", false)])));
        StringAssert.Contains(output.ToString(), "\"passed\": false");
    }

    [TestMethod]
    public void CancelledRunDoesNotLoadNativeCode()
    {
        Assert.AreEqual(130, DspCli.Run(["dsp-selftest", "--native-dir", Path.GetTempPath()],
            TextWriter.Null, TextWriter.Null, new CancellationToken(true)));
    }
}
