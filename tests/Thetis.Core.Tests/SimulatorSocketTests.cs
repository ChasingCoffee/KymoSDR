using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Thetis.Simulator;

namespace Thetis.Core.Tests;

[TestClass]
public sealed class SimulatorSocketTests
{
    [TestMethod]
    public async Task SelfTestUsesRealLoopbackDiscoveryControlsAndDdc2Samples()
    {
        var result = await SimulatorDiagnostics.RunAsync();
        Assert.IsTrue(result.Passed && result.LoopbackOnly);
        Assert.IsGreaterThanOrEqualTo(100, result.IqPackets);
        Assert.IsGreaterThan(0, result.MicPackets);
        Assert.IsGreaterThan(0, result.StatusPackets);
    }

    [TestMethod]
    public async Task ExistingDiscoveryServiceFindsSimulatorOnRealLoopbackSocket()
    {
        await using var simulator = G2Simulator.Open(new(BasePort: 0));
        var options = new RadioDiscoveryOptions
        {
            ProtocolMode = RadioDiscoveryProtocolMode.P2Only,
            FixedTargetIp = IPAddress.Loopback,
            AllowLoopback = true,
            IncludeGeneralBroadcast = false,
            DiscoveryPortBase = simulator.BasePort,
            MaxScanMilliseconds = 1500,
            ScanPerformance = ScanPerformanceProfile.UltraFast
        };
        var radios = new RadioDiscoveryService().discoverOnNic(IPAddress.Loopback, IPAddress.Parse("255.0.0.0"), options, out var diagnostics);
        Assert.HasCount(1, radios);
        Assert.IsFalse(diagnostics.SocketError);
    }

    [TestMethod]
    public async Task HundredLifecyclesReleaseEveryBoundPortAndDisposeIsIdempotent()
    {
        for (int cycle = 0; cycle < 100; ++cycle)
        {
            var simulator = G2Simulator.Open(new(BasePort: 0));
            int port = simulator.BasePort;
            await Task.WhenAll(simulator.DisposeAsync().AsTask(), simulator.DisposeAsync().AsTask());
            Assert.IsTrue(simulator.Completion.IsCompletedSuccessfully);
            foreach (int offset in P2Device.SocketOffsets)
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port + offset));
            }
        }
    }

    [TestMethod]
    public async Task PartialBindFailureRollsBackEarlierSockets()
    {
        int port;
        await using (var initial = G2Simulator.Open(new(BasePort: 0))) port = initial.BasePort;
        using (var occupied = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 13)))
            Assert.ThrowsExactly<SocketException>(() => G2Simulator.Open(new(BasePort: port)));
        await using var reopened = G2Simulator.Open(new(BasePort: port));
        Assert.AreEqual(port, reopened.BasePort);
    }

    [TestMethod]
    public async Task CancellationStopsAnIdleWorkerAndRejectsPrecancelledOpen()
    {
        Assert.ThrowsExactly<OperationCanceledException>(() => G2Simulator.Open(cancellationToken: new(true)));
        using var cancellation = new CancellationTokenSource();
        await using var simulator = G2Simulator.Open(new(BasePort: 0), cancellation.Token);
        cancellation.Cancel();
        await simulator.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(simulator.State.Running);
    }

    [TestMethod]
    public async Task CancellationDuringStreamingJoinsTheWorkerAndReleasesPorts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        await using var simulator = G2Simulator.Open(new(BasePort: 0), cancellation.Token);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = simulator.BasePort;
        await Send(0, SimulatorProtocolTests.General(port));
        await SimulatorDiagnostics.WaitFor(() => simulator.State.Configured, deadline.Token);
        await Send(1, SimulatorProtocolTests.Rx());
        await SimulatorDiagnostics.WaitFor(() => simulator.State.Receivers[2].Enabled, deadline.Token);
        await Send(3, SimulatorProtocolTests.High());
        await SimulatorDiagnostics.WaitFor(() => simulator.State.IqPackets > 0, deadline.Token);
        cancellation.Cancel();
        await simulator.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await simulator.DisposeAsync();
        Assert.IsFalse(simulator.State.Configured);
        using var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 13));

        async Task Send(int offset, byte[] data) =>
            await peer.SendAsync(data, new IPEndPoint(IPAddress.Loopback, port + offset), deadline.Token);
    }
}
