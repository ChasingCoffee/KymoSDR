using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class P1ReceiveCliTests
{
    [TestMethod]
    public async Task HelpSyntaxAndExitCodesPreserveLoopbackOnlyContract()
    {
        Assert.AreEqual(0,await P1ReceiveCli.RunAsync(["p1-receive-selftest","--help"],TextWriter.Null,TextWriter.Null));
        foreach (string[] args in new string[][] { ["p1-receive-selftest"], ["p1-receive-selftest","--native-dir","relative"],
            ["p1-receive-selftest","--radio","192.0.2.1"], ["p1-receive-selftest","--native-dir",Path.GetTempPath(),"--tx"] })
            Assert.AreEqual(2,await P1ReceiveCli.RunAsync(args,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new AssertFailedException()));
        string[] valid = ["p1-receive-selftest","--native-dir",Path.GetTempPath()];
        Assert.AreEqual(130,await P1ReceiveCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,new(true),(_,_) => throw new AssertFailedException()));
        Assert.AreEqual(3,await P1ReceiveCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new EntryPointNotFoundException()));
        Assert.AreEqual(4,await P1ReceiveCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new InvalidOperationException()));
    }
}
