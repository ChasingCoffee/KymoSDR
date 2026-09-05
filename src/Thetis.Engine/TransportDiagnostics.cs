using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Engine;

public sealed record TransportSelfTestResult(int SchemaVersion, DspAbiInfo Abi, bool Passed,
    int CompletedCycles, long ElapsedMilliseconds, bool LoopbackOnly, int ReceivedDatagrams, int OversizedDatagrams);

public static class TransportDiagnostics
{
    public static TransportSelfTestResult Run(string nativeDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        var abi = DspRuntime.Initialize(nativeDirectory);
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle();
            int packets = 0, oversized = 0;
            for (int cycle = 0; cycle < 100; ++cycle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var peer = OpenPeer();
                int remotePort = ((IPEndPoint)peer.Client.LocalEndPoint!).Port;
                using (var session = LoopbackTransportSession.Open(nativeDirectory,
                    new(RemotePort: remotePort), cancellationToken))
                {
                    var target = new IPEndPoint(IPAddress.Loopback, session.State.LocalPort);
                    foreach (int size in new[] { 0, 32, 1444, 2048 }) peer.Send(new byte[size], size, target);
                    var state = WaitForFixtures(session, cancellationToken);
                    if (state.ReceivedBytes != 1476 || state.Workers != 2 || state.SocketErrors != 0 || peer.Available != 0)
                        throw new InvalidOperationException("Loopback counts/ownership or no-send invariant failed.");
                    packets += state.Datagrams;
                    oversized += state.OversizedDatagrams;
                }
                var closed = LoopbackTransportSession.ReadNativeState();
                if (closed[1] != 0 || closed[7] != 0 || closed[8] != 0 || closed[2] != 0)
                    throw new InvalidOperationException("Transport resources remain after disposal.");
            }
            return new(1, abi, true, 100, clock.ElapsedMilliseconds, true, packets, oversized);
        }
    }

    internal static UdpClient OpenPeer()
    {
        // Remote radio configuration reserves an 18-port range. Avoid the few
        // ephemeral ports too high to fit that range, without fixed test ports.
        for (int attempt = 0; attempt < 100; ++attempt)
        {
            var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            if (((IPEndPoint)peer.Client.LocalEndPoint!).Port <= 65518) return peer;
            peer.Dispose();
        }
        throw new IOException("Could not obtain a loopback fixture port in range.");
    }

    internal static LoopbackTransportState WaitForFixtures(LoopbackTransportSession session, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            token.ThrowIfCancellationRequested();
            var state = session.State;
            if (state.SocketErrors != 0) throw new IOException("Native loopback receive worker failed.");
            if (state.Datagrams == 3 && state.OversizedDatagrams == 1 && state.TimerTicks > 0) return state;
            Thread.Sleep(5);
        }
        throw new TimeoutException("Loopback fixture datagrams or diagnostic timer did not advance.");
    }
}
