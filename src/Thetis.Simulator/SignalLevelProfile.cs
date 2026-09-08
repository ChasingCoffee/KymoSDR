namespace Thetis.Simulator;

public sealed record SignalLevelStep(int Milliseconds, double Amplitude);

/// <summary>Immutable sample-clock amplitude steps; not wall-clock timers. Phase stays
/// continuous, and the profile restarts with each receiver stream/rate change.</summary>
public sealed class SignalLevelProfile
{
    private readonly SignalLevelStep[] steps;
    public SignalLevelProfile(params SignalLevelStep[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(steps));
        this.steps = (SignalLevelStep[])steps.Clone();
        int previous = -1;
        foreach (var step in this.steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (step.Milliseconds <= previous || step.Milliseconds > 600000) throw new ArgumentOutOfRangeException(nameof(steps));
            ValidateAmplitude(step.Amplitude); previous = step.Milliseconds;
        }
    }
    internal static void ValidateAmplitude(double amplitude)
    {
        if (!double.IsFinite(amplitude) || amplitude is < 0 or > .9) throw new ArgumentOutOfRangeException(nameof(amplitude));
    }
    internal double AtSample(long sample, int rate, double initial)
    {
        int lo = 0, hi = steps.Length;
        while (lo < hi)
        {
            int mid = lo + (hi-lo)/2;
            if (sample >= (long)steps[mid].Milliseconds * rate / 1000) lo = mid + 1;
            else hi = mid;
        }
        return lo == 0 ? initial : steps[lo-1].Amplitude;
    }
}
