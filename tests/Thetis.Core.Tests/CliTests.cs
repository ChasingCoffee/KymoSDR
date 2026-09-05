using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Headless;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class CliTests
{
    [TestMethod]
    public void NoArgumentsShowHelpWithoutNetworking()
    {
        var backend = new FakeBackend();
        using var output = new StringWriter();
        int code = DiscoveryCli.Run([], output, TextWriter.Null, backend);
        Assert.AreEqual(0, code);
        StringAssert.Contains(output.ToString(), "does not start RX or TX");
        Assert.AreEqual(0, backend.Calls);
    }

    [TestMethod]
    public void OptionsMapToExistingDiscoveryService()
    {
        var cli = DiscoveryCli.Parse(["discover", "--nic", "192.0.2.10", "--target", "192.0.2.20",
            "--protocol", "p2", "--port", "12345", "--local-port", "12346", "--timeout-ms", "250",
            "--profile", "safe", "--allow-loopback", "--include-other", "--ignore-subnet", "--no-general-broadcast"]);
        Assert.AreEqual(IPAddress.Parse("192.0.2.10"), cli.Options.FixedLocalIp);
        Assert.AreEqual(IPAddress.Parse("192.0.2.20"), cli.Options.FixedTargetIp);
        Assert.AreEqual(RadioDiscoveryProtocolMode.P2Only, cli.Options.ProtocolMode);
        Assert.AreEqual(12345, cli.Options.DiscoveryPortBase);
        Assert.AreEqual(12346, cli.Options.BindLocalPort);
        Assert.AreEqual(250, cli.Options.MaxScanMilliseconds);
        Assert.AreEqual(ScanPerformanceProfile.Safe, cli.Options.ScanPerformance);
        Assert.IsTrue(cli.Options.AllowLoopback);
        Assert.IsTrue(cli.Options.IncludeOtherInterfaceTypes);
        Assert.IsTrue(cli.Options.IgnoreSubnetCheck);
        Assert.IsFalse(cli.Options.IncludeGeneralBroadcast);
    }

    [TestMethod]
    [DataRow("--port", "0")]
    [DataRow("--port", "65536")]
    [DataRow("--timeout-ms", "0")]
    [DataRow("--timeout-ms", "60001")]
    [DataRow("--nic", "::1")]
    [DataRow("--nic", "0.0.0.0")]
    [DataRow("--target", "255.255.255.255")]
    [DataRow("--protocol", "p3")]
    [DataRow("--profile", "unknown")]
    [DataRow("--unknown", "x")]
    public void InvalidOptionsHaveJsonErrorAndDoNotUseNetwork(string option, string value)
    {
        var backend = new FakeBackend();
        var (code, report) = Run(["discover", option, value, "--json"], backend);
        using (report)
        {
            Assert.AreEqual(2, code);
            Assert.AreEqual("invalidArguments", report.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(0, backend.Calls);
        }
    }

    [TestMethod]
    public void MissingAndRepeatedValuesAreRejected()
    {
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryCli.Parse(["discover", "--port"]));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryCli.Parse(["discover", "--nic", "--json"]));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryCli.Parse(["discover", "--port", "1", "--port", "2"]));
        Assert.ThrowsExactly<ArgumentException>(() => DiscoveryCli.Parse(["receive"]));
    }

    [TestMethod]
    public void EmptyScanIsNoRadioNotSuccess()
    {
        var (code, report) = Run(["discover", "--json"], new FakeBackend { Results = [Nic()] });
        using (report)
        {
            Assert.AreEqual(1, code);
            Assert.AreEqual("noRadio", report.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(1, report.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    [TestMethod]
    public void NoEligibleInterfaceIsNetworkError()
    {
        var (code, report) = Run(["discover", "--json"], new FakeBackend());
        using (report) Assert.AreEqual(3, code);
    }

    [TestMethod]
    public void NicsSelectionDoesNotCallDiscover()
    {
        var backend = new FakeBackend { Results = [Nic(), Nic("192.0.2.11")] };
        var (code, report) = Run(["nics", "--nic", "192.0.2.11", "--json"], backend);
        using (report)
        {
            Assert.AreEqual(0, code);
            Assert.AreEqual(0, backend.DiscoverCalls);
            var interfaces = report.RootElement.GetProperty("interfaces");
            Assert.AreEqual(1, interfaces.GetArrayLength());
            Assert.AreEqual("192.0.2.11", interfaces[0].GetProperty("localAddress").GetString());
        }
    }

    [TestMethod]
    public void RadioAndDeadlineAreSerializedWithoutPlatformIpAddressObjects()
    {
        var nic = Nic();
        nic.Diagnostics.DeadlineReached = true;
        nic.Radios.Add(new RadioInfo
        {
            Protocol = RadioDiscoveryRadioProtocol.P2, DeviceType = HPSDRHW.Saturn,
            IpAddress = IPAddress.Parse("192.0.2.20"), MacAddress = "02-00-00-00-00-02",
            CodeVersion = 42, BetaVersion = 3, NumRxs = 10, DiscoveryPortBase = 1024, PortCount = 18
        });
        var (code, report) = Run(["discover", "--json"], new FakeBackend { Results = [nic] });
        using (report)
        {
            Assert.AreEqual(0, code);
            Assert.IsTrue(report.RootElement.GetProperty("deadlineReached").GetBoolean());
            var radio = report.RootElement.GetProperty("interfaces")[0].GetProperty("radios")[0];
            Assert.AreEqual("Saturn", radio.GetProperty("deviceType").GetString());
            Assert.AreEqual("192.0.2.20", radio.GetProperty("address").GetString());
            Assert.AreEqual(42, radio.GetProperty("firmwareCode").GetInt32());
        }
    }

    [TestMethod]
    public void PartialSocketFailureIsNotSilentSuccess()
    {
        var nic = Nic();
        nic.Diagnostics.SocketError = true;
        nic.Diagnostics.SocketErrorCode = "AccessDenied";
        nic.Diagnostics.ErrorMessage = "Synthetic failure";
        nic.Radios.Add(new RadioInfo { IpAddress = IPAddress.Parse("192.0.2.20") });
        var (code, report) = Run(["discover", "--json"], new FakeBackend { Results = [nic] });
        using (report)
        {
            Assert.AreEqual(3, code);
            Assert.AreEqual("networkError", report.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(1, report.RootElement.GetProperty("interfaces")[0].GetProperty("radios").GetArrayLength());
        }
    }

    [TestMethod]
    public void CancelledInvocationReturns130WithoutNetworking()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var backend = new FakeBackend();
        var (code, report) = Run(["discover", "--json"], backend, cancellation.Token);
        using (report)
        {
            Assert.AreEqual(130, code);
            Assert.AreEqual("cancelled", report.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(0, backend.Calls);
        }
    }

    [TestMethod]
    public void SocketExceptionAtEnumerationIsNetworkError()
    {
        var (code, report) = Run(["nics", "--json"], new FakeBackend { Failure = new SocketException((int)SocketError.AccessDenied) });
        using (report) Assert.AreEqual(3, code);
    }

    private static NicRadioScanResult Nic(string address = "192.0.2.10") => new()
    {
        NicName = "test-nic", NicId = "test-id", NicDescription = "Synthetic test interface",
        LocalIPv4 = IPAddress.Parse(address), LocalMaskIPv4 = IPAddress.Parse("255.255.255.0"),
        NicInterfaceType = System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
        Diagnostics = new DiscoveryDiagnostics()
    };

    private static (int Code, JsonDocument Report) Run(string[] args, FakeBackend backend, CancellationToken cancellation = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int code = DiscoveryCli.Run(args, output, error, backend, cancellation);
        Assert.AreEqual("", error.ToString(), "JSON mode must keep its structured result on stdout.");
        return (code, JsonDocument.Parse(output.ToString()));
    }

    private sealed class FakeBackend : IDiscoveryBackend
    {
        public List<NicRadioScanResult> Results { get; init; } = [];
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public int DiscoverCalls { get; private set; }
        public List<NicRadioScanResult> List(RadioDiscoveryOptions options)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Results;
        }
        public List<NicRadioScanResult> Discover(RadioDiscoveryOptions options, CancellationToken cancellation)
        {
            DiscoverCalls++;
            return List(options);
        }
    }
}
