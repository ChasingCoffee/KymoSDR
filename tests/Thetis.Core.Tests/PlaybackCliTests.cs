using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;
[TestClass]
public sealed class PlaybackCliTests
{
    [TestMethod]
    public async Task SyntaxNeverImplicitlyOpensADeviceOrSelectsHardware()
    {
        foreach (string[] args in new string[][] { ["playback-selftest"], ["playback-selftest","--native-dir","relative"],
            ["playback-selftest","--native-dir",Path.GetTempPath(),"--device","0"], ["playback-listen","--native-dir",Path.GetTempPath()],
            ["playback-listen","--native-dir",Path.GetTempPath(),"--device","0","--seconds","31"],
            ["playback-listen","--native-dir",Path.GetTempPath(),"--device","0","--device","1"],
            ["playback-listen","--native-dir",Path.GetTempPath(),"--radio","192.0.2.1"],
            ["audio-devices","--native-dir",Path.GetTempPath(),"--tx"] })
            Assert.AreEqual(2,await PlaybackCli.RunAsync(args,TextWriter.Null,TextWriter.Null));
        foreach (string command in new[] {"playback-selftest","audio-devices","playback-listen"})
            Assert.AreEqual(0,await PlaybackCli.RunAsync([command,"--help"],TextWriter.Null,TextWriter.Null));
        Assert.AreEqual(130,await PlaybackCli.RunAsync(["playback-selftest","--native-dir",Path.GetTempPath()],TextWriter.Null,TextWriter.Null,new(true)));
    }
}
