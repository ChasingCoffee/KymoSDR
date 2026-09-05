using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class TransportCliTests
{
    [TestMethod]
    public void HelpAndInvalidOptionsDoNotLoadNativeCode()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, TransportCli.Run(["transport-selftest", "--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "loopback-only");
        Assert.AreEqual(2, TransportCli.Run(["transport-selftest"], TextWriter.Null, TextWriter.Null));
        Assert.AreEqual(2, TransportCli.Run(["transport-selftest", "--native-dir", "relative"], TextWriter.Null, TextWriter.Null));
        Assert.AreEqual(2, TransportCli.Run(["transport-selftest", "--radio", "192.0.2.1"], TextWriter.Null, TextWriter.Null));
    }

    [TestMethod]
    public void CancellationAndFailuresHaveDistinctExitCodes()
    {
        string[] args = ["transport-selftest", "--native-dir", Path.GetTempPath()];
        Assert.AreEqual(130, TransportCli.Run(args, TextWriter.Null, TextWriter.Null, new(true)));
        Assert.AreEqual(3, TransportCli.Run(args, TextWriter.Null, TextWriter.Null, runner: (_, _) => throw new DllNotFoundException("missing")));
        Assert.AreEqual(4, TransportCli.Run(args, TextWriter.Null, TextWriter.Null, runner: (_, _) => throw new InvalidOperationException("failed")));
    }

    [TestMethod]
    public void SuccessfulResultIncludesLoopbackBoundaryAndCounters()
    {
        using var output = new StringWriter();
        string path = Path.GetTempPath();
        Assert.AreEqual(0, TransportCli.Run(["transport-selftest", "--native-dir", path], output, TextWriter.Null,
            runner: (_, _) => new(1, new(path, 200, 8, 4, 8, 4, 480), true, 100, 5678, true, 300, 100)));
        StringAssert.Contains(output.ToString(), "\"completedCycles\": 100");
        StringAssert.Contains(output.ToString(), "\"loopbackOnly\": true");
        StringAssert.Contains(output.ToString(), "\"receivedDatagrams\": 300");
    }
}
