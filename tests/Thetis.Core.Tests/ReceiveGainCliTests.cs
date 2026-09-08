using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class ReceiveGainCliTests
{
    [TestMethod]
    public async Task SyntaxAndErrorCodesCannotSelectHardwareOrTransmit()
    {
        Assert.AreEqual(0,await ReceiveGainCli.RunAsync(["receive-gain-selftest","--help"],TextWriter.Null,TextWriter.Null));
        foreach (string[] args in new string[][] { ["receive-gain-selftest"], ["receive-gain-selftest","--native-dir","relative"],
            ["receive-gain-selftest","--radio","192.0.2.1"], ["receive-gain-selftest","--native-dir",Path.GetTempPath(),"--tx"] })
            Assert.AreEqual(2,await ReceiveGainCli.RunAsync(args,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new AssertFailedException()));
        string[] valid = ["receive-gain-selftest","--native-dir",Path.GetTempPath()];
        Assert.AreEqual(130,await ReceiveGainCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,new(true),(_,_) => throw new AssertFailedException()));
        Assert.AreEqual(3,await ReceiveGainCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new EntryPointNotFoundException()));
        Assert.AreEqual(4,await ReceiveGainCli.RunAsync(valid,TextWriter.Null,TextWriter.Null,runner: (_,_) => throw new InvalidOperationException()));
    }
}
