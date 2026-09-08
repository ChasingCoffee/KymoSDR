namespace Thetis.Engine;

public enum ReceiveAgcMode { Off = 0, Slow = 2, Medium = 3, Fast = 4 }

/// <summary>Post-AGC audio attenuation/mute and WDSP AGC maximum gain. No RF hardware gain.
/// AGC off retains fixed unity gain. Defaults preserve the original receive measurements.</summary>
public sealed record ReceiveGain(int AudioGainDb = 0, bool Muted = false,
    ReceiveAgcMode AgcMode = ReceiveAgcMode.Off, int AgcMaxGainDb = 60)
{
    internal void Validate()
    {
        if (AudioGainDb is < -60 or > 0) throw new ArgumentOutOfRangeException(nameof(AudioGainDb));
        if (AgcMode is not (ReceiveAgcMode.Off or ReceiveAgcMode.Slow or ReceiveAgcMode.Medium or ReceiveAgcMode.Fast))
            throw new ArgumentOutOfRangeException(nameof(AgcMode));
        if (AgcMaxGainDb is < 0 or > 80) throw new ArgumentOutOfRangeException(nameof(AgcMaxGainDb));
    }
}

/// <summary>Applied preset timing, not measured settling latency. Audio generation is not sample-accurate.</summary>
public sealed record ReceiveGainState(ReceiveGain Settings, int AttackMilliseconds, int DecayMilliseconds,
    int HangMilliseconds, int HangThresholdPercent, long Generation);
