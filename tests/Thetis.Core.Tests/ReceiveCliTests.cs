using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class ReceiveCliTests
{
    [TestMethod]
    public async Task HelpAndInvalidOptionsCannotStartSocketsOrLoadNativeCode()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, await ReceiveCli.RunAsync(["receive-selftest", "--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "loopback simulator");
        foreach (string[] args in new string[][]
        {
            ["receive-selftest"], ["receive-selftest", "--native-dir", "relative"],
            ["receive-selftest", "--radio", "192.0.2.1"],
            ["receive-selftest", "--native-dir", Path.GetTempPath(), "--tx-mode", "sink"]
        }) Assert.AreEqual(2, await ReceiveCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new AssertFailedException("Must not run.")));
    }

    [TestMethod]
    public async Task CancellationLoadFailureAndExecutionFailureHaveDistinctExitCodes()
    {
        string[] args = ["receive-selftest", "--native-dir", Path.GetTempPath()];
        Assert.AreEqual(130, await ReceiveCli.RunAsync(args, TextWriter.Null, TextWriter.Null, new(true),
            runner: (_, _) => throw new AssertFailedException("Must not run.")));
        Assert.AreEqual(3, await ReceiveCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new EntryPointNotFoundException("old library")));
        Assert.AreEqual(4, await ReceiveCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new TimeoutException("No audio")));
    }
}
