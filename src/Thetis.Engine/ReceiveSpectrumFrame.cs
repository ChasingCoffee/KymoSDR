namespace Thetis.Engine;

/// <summary>A copied, renderer-independent snapshot of RX0's pre-demodulation WDSP spectrum.</summary>
public sealed class ReceiveSpectrumFrame
{
    internal const int NativePixelCount = 4095;
    internal ReceiveSpectrumFrame(float[] pixels, long[] metadata)
    {
        if (metadata.Length != 12 || pixels.Length != NativePixelCount ||
            metadata[0] != 1 || metadata[7] != 4096 || metadata[8] != NativePixelCount)
            throw new NotSupportedException("Native receive spectrum layout does not match ABI 1.");
        LevelsDb = pixels;
        Sequence = metadata[1]; TuningGeneration = metadata[2]; PublishedMonotonicMilliseconds = metadata[3];
        RequestedCenterFrequencyHz = (int)metadata[4]; Ddc = (int)metadata[5]; SampleRate = (int)metadata[6];
        CoalescedFrames = metadata[9]; MissingPackets = metadata[10]; InputOverruns = metadata[11];
    }

    /// <summary>Uncalibrated dB relative to unit complex amplitude, not dBm or dB/Hz.
    /// Hann-windowed, without averaging. This storage is not reused by subsequent pulls.</summary>
    public ReadOnlyMemory<float> LevelsDb { get; }
    public long Sequence { get; }
    /// <summary>Starts at 1; each Tune call increments it and invalidates pending frames.</summary>
    public long TuningGeneration { get; }
    /// <summary>Native host monotonic publication time, not UTC or a radio sample timestamp.</summary>
    public long PublishedMonotonicMilliseconds { get; }
    /// <summary>Requested tuning, not hardware-confirmed. P2 supplies no sample-accurate tune acknowledgement.</summary>
    public int RequestedCenterFrequencyHz { get; }
    public int Ddc { get; }
    public int SampleRate { get; }
    public int FftSize => 4096;
    public bool IsCalibrated => false;
    public double BinWidthHz => (double)SampleRate / FftSize;
    public double FirstFrequencyHz => RequestedCenterFrequencyHz - SampleRate / 2.0 + BinWidthHz;
    /// <summary>Cumulative unread frames replaced in this session; retune invalidations are not counted.</summary>
    public long CoalescedFrames { get; }
    /// <summary>Cumulative transport gaps at publication, not a per-FFT discontinuity map.</summary>
    public long MissingPackets { get; }
    public long InputOverruns { get; }

    /// <summary>Exact bin-center axis. WDSP's 4095-pixel mapping omits the negative Nyquist bin.</summary>
    public double FrequencyAt(int pixel)
    {
        if ((uint)pixel >= (uint)LevelsDb.Length) throw new ArgumentOutOfRangeException(nameof(pixel));
        return FirstFrequencyHz + pixel * BinWidthHz;
    }
}
