using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class G2ListenCliTests
{
    private static string[] Args => ["g2-listen","--native-dir",Path.GetTempPath(),"--nic","169.254.47.65",
        "--target","169.254.187.120","--mac","2c:cf:67:fc:a3:df","--confirm-ant1-rx"];
    [TestMethod]
    public async Task ListeningRequiresExplicitHardwareAndDeviceOptIns()
    {
        var options = G2ListenCli.Parse(Args);
        Assert.IsNull(options.Device); Assert.IsFalse(options.Unmute); Assert.IsNull(options.CapturePath);
        Assert.AreEqual(14_074_000,options.Receive.FrequencyHz);
        Assert.AreEqual(Thetis.Engine.ReceiveMode.Usb,options.Mode);
        Assert.AreEqual(-40,options.AudioGainDb); Assert.AreEqual(60,options.AgcMaxGainDb);
        var gain = G2ListenCli.Parse([..Args,"--af-db","-20","--agc-max-db","80"]);
        Assert.AreEqual(-20,gain.AudioGainDb); Assert.AreEqual(80,gain.AgcMaxGainDb);
        Assert.AreEqual(Thetis.Engine.ReceiveMode.Lsb,G2ListenCli.Parse([..Args,"--mode","lsb"]).Mode);
        using var help = new StringWriter();
        Assert.AreEqual(0,await G2ListenCli.RunAsync(["g2-listen","--help"],help,TextWriter.Null));
        foreach (string[] bad in new string[][] { Args[..^1], [..Args,"--device","-1"], [..Args,"--ptt"],
            [..Args,"--mode","tx"], [..Args,"--mode","usb","--mode","lsb"],
            [..Args,"--af-db","1"], [..Args,"--af-db","-61"], [..Args,"--agc-max-db","81"],
            [..Args,"--unmute","--unmute"], [..Args,"--device","0","--device","1"], [..Args,"--capture","relative.wav"],
            [..Args,"--capture",Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav")] })
            Assert.AreEqual(2,await G2ListenCli.RunAsync(bad,TextWriter.Null,TextWriter.Null,
                runner: (_,_) => throw new AssertFailedException("Invalid input reached hardware runner.")));
        Assert.AreEqual(130,await G2ListenCli.RunAsync(Args,TextWriter.Null,TextWriter.Null,new(true),
            runner: (_,_) => throw new AssertFailedException("Cancellation reached hardware runner.")));
    }
}
