using System.Net;
using System.Net.NetworkInformation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Engine;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class G2ReceiveCliTests
{
    private static string[] Args => ["g2-receive", "--native-dir", Path.GetTempPath(), "--nic", "169.254.47.65",
        "--target", "169.254.187.120", "--mac", "2c:cf:67:fc:a3:df", "--confirm-ant1-rx"];
    private static NicRadioScanResult Fixture() => new()
    {
        NicName = "en7", NicInterfaceType = NetworkInterfaceType.Ethernet,
        LocalIPv4 = IPAddress.Parse("169.254.47.65"), LocalMaskIPv4 = IPAddress.Parse("255.255.0.0"),
        IsApipaLocal = true, Diagnostics = new(),
        Radios = [new() { IpAddress = IPAddress.Parse("169.254.187.120"), MacAddress = "2C-CF-67-FC-A3-DF",
            DeviceType = (HPSDRHW)10, Protocol = RadioDiscoveryRadioProtocol.P2, CodeVersion = 27,
            BetaVersion = 50, Protocol2Supported = 43, NumRxs = 10, DiscoveryPortBase = 1024 }]
    };

    [TestMethod]
    public async Task HelpAndInvalidInputNeverReachNetworkOrNativeRunner()
    {
        using var text = new StringWriter();
        Assert.AreEqual(0, await G2ReceiveCli.RunAsync(["g2-receive","--help"],text,TextWriter.Null));
        StringAssert.Contains(text.ToString(),"No Wi-Fi fallback");
        foreach (string[] args in new string[][]
        {
            ["g2-receive"], Args[..^1], [..Args,"--ptt"], [..Args,"--tx"], [..Args,"--confirm-ant1-rx"],
            [..Args,"--nic","192.168.1.153"], [..Args,"--duration-seconds","0"], [..Args,"--duration-seconds","61"],
            [..Args,"--duration-seconds","5.0"], [..Args,"--frequency-hz","7000000"], [..Args,"--frequency-hz"]
        }) Assert.AreEqual(2, await G2ReceiveCli.RunAsync(args,TextWriter.Null,TextWriter.Null,
            runner: (_,_) => throw new AssertFailedException("Unsafe arguments reached runner.")));
        foreach (var (key,value) in new[] { ("--target","anan-g2.local"), ("--target","127.0.0.1"),
            ("--target","255.255.255.255"), ("--target","::1"), ("--nic","0.0.0.0"),
            ("--native-dir","relative"), ("--mac","ff:ff:ff:ff:ff:ff"), ("--mac","00:00:00:00:00:00") })
        {
            var args = Args; args[Array.IndexOf(args,key)+1] = value;
            Assert.AreEqual(2, await G2ReceiveCli.RunAsync(args,TextWriter.Null,TextWriter.Null,
                runner: (_,_) => throw new AssertFailedException("Unsafe arguments reached runner.")));
        }
    }

    [TestMethod]
    public void PreflightAcceptsExactEthernetProfileAndRejectsFallbackBusyAndMismatchedIdentity()
    {
        var options = G2ReceiveCli.Parse(Args); var nic = Fixture();
        Assert.AreEqual("2C-CF-67-FC-A3-DF",options.MacAddress);
        Assert.AreSame(nic.Radios[0],G2ReceiveCli.SelectRadio([nic],options,true));
        foreach (Action<NicRadioScanResult> change in new Action<NicRadioScanResult>[]
        {
            n => n.NicInterfaceType = NetworkInterfaceType.Wireless80211,
            n => n.LocalIPv4 = IPAddress.Parse("192.168.1.153"), n => n.LocalMaskIPv4 = IPAddress.Parse("255.255.255.0"),
            n => n.Diagnostics.SocketError = true, n => n.Diagnostics.DeadlineReached = true,
            n => n.Radios[0].IsBusy = true, n => n.Radios[0].MacAddress = "02-00-00-00-00-01",
            n => n.Radios[0].DeviceType = (HPSDRHW)6, n => n.Radios[0].Protocol = RadioDiscoveryRadioProtocol.P1,
            n => n.Radios[0].CodeVersion = 28, n => n.Radios[0].Protocol2Supported = 42,
            n => n.Radios[0].NumRxs = 2, n => n.Radios[0].DiscoveryPortBase = 51024,
            n => n.Radios[0].IpAddress = IPAddress.Parse("169.254.187.121"), n => n.Radios.Clear()
        })
        {
            nic = Fixture(); change(nic);
            Assert.Throws<Exception>(() => G2ReceiveCli.SelectRadio([nic],options,true));
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => G2ReceiveCli.SelectRadio([Fixture(),Fixture()],options,true));
        Assert.ThrowsExactly<InvalidOperationException>(() => G2ReceiveCli.SelectInterface([],options));
    }

    [TestMethod]
    public void HardwareOptionsRejectUnsafeEndpointsAndOutOfProfileBeforeNativeLoad()
    {
        var valid = new G2ReceiveOptions("169.254.187.120","169.254.47.65","255.255.0.0");
        valid.Validate();
        foreach (var options in new[]
        {
            valid with { RadioAddress = "127.0.0.1" }, valid with { RadioAddress = "anan-g2.local" },
            valid with { RadioAddress = "169.254.255.255" }, valid with { RadioAddress = "169.254.0.0" },
            valid with { RadioAddress = "169.254.47.65" }, valid with { RadioAddress = "192.168.1.210" },
            valid with { LocalAddress = "224.0.0.1" }, valid with { LocalAddress = "0.0.0.0" },
            valid with { SubnetMask = "255.0.255.0" }, valid with { SubnetMask = "0.0.0.0" },
            valid with { SubnetMask = "255.255.255.255" }, valid with { SubnetMask = "255.255.255.254" },
            valid with { FrequencyHz = 13999999 }, valid with { FrequencyHz = 14350001 },
            valid with { DurationSeconds = 0 }, valid with { DurationSeconds = 61 }
        }) Assert.ThrowsExactly<ArgumentException>(() => G2ReceiveSession.Open("unused",options));
        Assert.ThrowsExactly<OperationCanceledException>(() => G2ReceiveSession.Open("unused",valid,new(true)));
        Assert.ThrowsExactly<ArgumentException>(() => P2ReceiveSession.Open("unused",new(1024,Address:valid.RadioAddress)));
    }

    [TestMethod]
    public async Task CancellationAndFailuresHaveDistinctExitCodes()
    {
        Assert.AreEqual(130,await G2ReceiveCli.RunAsync(Args,TextWriter.Null,TextWriter.Null,new(true),
            runner: (_,_) => throw new AssertFailedException("Must not run.")));
        Assert.AreEqual(3,await G2ReceiveCli.RunAsync(Args,TextWriter.Null,TextWriter.Null,
            runner: (_,_) => throw new EntryPointNotFoundException()));
        Assert.AreEqual(4,await G2ReceiveCli.RunAsync(Args,TextWriter.Null,TextWriter.Null,
            runner: (_,_) => throw new InvalidOperationException("Busy")));
    }
}
