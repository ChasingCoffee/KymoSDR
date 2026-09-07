using System.Runtime.InteropServices;

namespace Thetis.Simulator;

/// <summary>Balanced Windows timer request, owned only by a live simulator.</summary>
internal sealed class SimulatorTimerResolution : IDisposable
{
    private Func<uint, uint>? release;
    private SimulatorTimerResolution() { }

    internal static SimulatorTimerResolution Acquire() =>
        Acquire(OperatingSystem.IsWindows(), timeBeginPeriod, timeEndPeriod);

    internal static SimulatorTimerResolution Acquire(bool windows, Func<uint, uint> begin, Func<uint, uint> end)
    {
        var scope = new SimulatorTimerResolution();
        if (!windows) return scope; // Never load WinMM on macOS/Linux.
        if (begin(1) != 0) throw new InvalidOperationException("Cannot request the simulator's 1 ms Windows timer resolution.");
        scope.release = end;
        return scope;
    }

    public void Dispose()
    {
        var end = Interlocked.Exchange(ref release, null);
        if (end is not null && end(1) != 0)
            throw new InvalidOperationException("Cannot release the simulator's Windows timer resolution request.");
    }

    [DllImport("winmm.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint timeEndPeriod(uint period);
}
