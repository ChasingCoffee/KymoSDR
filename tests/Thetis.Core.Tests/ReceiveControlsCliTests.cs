using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class ReceiveControlsCliTests
{
    [TestMethod]
    public async Task HelpAndInvalidArgumentsCannotStartNativeOrSockets()
    {
        using var output = new StringWriter();
        Assert.AreEqual(0, await ReceiveControlsCli.RunAsync(["receive-controls-selftest", "--help"], output, TextWriter.Null));
        StringAssert.Contains(output.ToString(), "loopback simulator");
        foreach (string[] args in new string[][] { ["receive-controls-selftest"], ["receive-controls-selftest", "--native-dir", "relative"],
            ["receive-controls-selftest", "--radio", "192.0.2.1"], ["receive-controls-selftest", "--native-dir", Path.GetTempPath(), "--tx"] })
            Assert.AreEqual(2, await ReceiveControlsCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
                runner: (_, _) => throw new AssertFailedException("Must not execute.")));
    }

    [TestMethod]
    public async Task CancellationOldLibrariesAndMeasurementFailuresHaveDistinctExitCodes()
    {
        string[] args = ["receive-controls-selftest", "--native-dir", Path.GetTempPath()];
        Assert.AreEqual(130, await ReceiveControlsCli.RunAsync(args, TextWriter.Null, TextWriter.Null, new(true),
            (_, _) => throw new AssertFailedException("Must not execute.")));
        Assert.AreEqual(3, await ReceiveControlsCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new EntryPointNotFoundException("Old controls ABI")));
        Assert.AreEqual(4, await ReceiveControlsCli.RunAsync(args, TextWriter.Null, TextWriter.Null,
            runner: (_, _) => throw new InvalidOperationException("Passband failed")));
    }
}
