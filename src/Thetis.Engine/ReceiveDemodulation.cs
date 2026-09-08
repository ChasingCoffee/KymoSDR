namespace Thetis.Engine;

public enum ReceiveMode { Lsb = 0, Usb = 1 }

/// <summary>Positive audio-frequency filter edges; LSB maps to negative baseband frequencies.
/// Initial SSB contract: 0..12000 Hz, at least 100 Hz wide. AGC/gain stay fixed.</summary>
public sealed record ReceiveDemodulation(ReceiveMode Mode = ReceiveMode.Usb, int LowCutHz = 300, int HighCutHz = 3000)
{
    internal void Validate()
    {
        if (Mode is not (ReceiveMode.Lsb or ReceiveMode.Usb)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (LowCutHz < 0 || HighCutHz > 12000 || HighCutHz <= LowCutHz || HighCutHz - LowCutHz < 100)
            throw new ArgumentOutOfRangeException(nameof(LowCutHz), "SSB edges must satisfy 0 <= low < high <= 12000 Hz with width >= 100 Hz.");
    }
    public int SignedLowHz => Mode == ReceiveMode.Usb ? LowCutHz : -HighCutHz;
    public int SignedHighHz => Mode == ReceiveMode.Usb ? HighCutHz : -LowCutHz;
}

/// <summary>Applied control state, not a sample-accurate transition marker.</summary>
public sealed record ReceiveDemodulationState(ReceiveDemodulation Settings, long Generation);
