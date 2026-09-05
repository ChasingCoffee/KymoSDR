using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SessionCliTests
{
    [TestMethod]
    public void HelpAndInvalidOptionsDoNotLoadNativeCode()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, SessionCli.Run(["session-selftest", "--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "No network");
        Assert.AreEqual(2, SessionCli.Run(["session-selftest"], TextWriter.Null, TextWriter.Null));
        Assert.AreEqual(2, SessionCli.Run(["session-selftest", "--native-dir", "relative"], TextWriter.Null, TextWriter.Null));
    }

    [TestMethod]
    public void PreCancellationAndLoadFailureHaveDistinctExitCodes()
    {
        string[] args = ["session-selftest", "--native-dir", Path.GetTempPath()];
        Assert.AreEqual(130, SessionCli.Run(args, TextWriter.Null, TextWriter.Null, new(true)));
        Assert.AreEqual(3, SessionCli.Run(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new DllNotFoundException("missing test dependency")));
        Assert.AreEqual(4, SessionCli.Run(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new InvalidOperationException("startup failed")));
    }

    [TestMethod]
    public void SuccessfulResultIncludesCompletedCycles()
    {
        using var output = new StringWriter();
        string path = Path.GetTempPath();
        Assert.AreEqual(0, SessionCli.Run(["session-selftest", "--native-dir", path], output, TextWriter.Null,
            runner: (_, _) => new(1, new(path, 200, 8, 4, 8, 4, 480), true, 100, 1234,
                new(8, 5, 2, 1, 2, 192000, 48000, 192000, 18, 1, 5, 5), 1024, 1024)));
        StringAssert.Contains(output.ToString(), "\"completedCycles\": 100");
    }
}
