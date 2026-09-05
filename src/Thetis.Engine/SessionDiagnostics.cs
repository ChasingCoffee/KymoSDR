using System.Diagnostics;

namespace Thetis.Engine;

public sealed record SessionSelfTestResult(int SchemaVersion, DspAbiInfo Abi, bool Passed,
    int CompletedCycles, long ElapsedMilliseconds, OfflineSessionState Topology,
    long WarmResidentBytes, long FinalResidentBytes);

public static class SessionDiagnostics
{
    public static SessionSelfTestResult Run(string nativeDirectory, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var abi = DspRuntime.Initialize(nativeDirectory);
        lock (DspRuntime.Gate)
        {
            OfflineRadioSession.RequireIdle();
            OfflineSessionState? topology = null;
            long warm = 0;
            for (int cycle = 0; cycle < 100; ++cycle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var session = OfflineRadioSession.Open(nativeDirectory, cancellationToken: cancellationToken))
                {
                    topology = session.State;
                    if (topology != new OfflineSessionState(8, 5, 2, 1, 2, 192000, 48000, 192000, 18, 1, 5, 5))
                        throw new InvalidOperationException("Unexpected offline ChannelMaster topology or worker count.");
                }
                var closed = OfflineRadioSession.ReadNativeState();
                if (closed[1] != 0 || closed[10] != 0)
                    throw new InvalidOperationException("ChannelMaster workers remain after disposal.");
                if (cycle == 9) warm = ResidentBytes();
            }
            // RSS is diagnostic, not a portable leak detector. CI also runs
            // native 100-cycle tests under ASan/UBSan/LeakSanitizer.
            return new(1, abi, true, 100, timer.ElapsedMilliseconds, topology!, warm, ResidentBytes());
        }
    }

    private static long ResidentBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }
}
