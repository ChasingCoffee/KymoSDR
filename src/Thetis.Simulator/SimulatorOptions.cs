namespace Thetis.Simulator;

public sealed record SimulatorOptions(int BasePort = 51024, double ToneFrequencyHz = 14_200_000,
    double Amplitude = 0.25, double NoiseAmplitude = 0, uint Seed = 1,
    int DropEvery = 0, int LeaseTimeoutMilliseconds = 2000, bool SimulateTransmit = false)
{
    internal void Validate()
    {
        // Zero asks the socket owner to reserve an available high-port layout.
        if (BasePort != 0 && BasePort is < 1024 or > 65515) throw new ArgumentOutOfRangeException(nameof(BasePort));
        if (!double.IsFinite(ToneFrequencyHz) || ToneFrequencyHz is < 0 or > 61_440_000)
            throw new ArgumentOutOfRangeException(nameof(ToneFrequencyHz));
        if (!double.IsFinite(Amplitude) || Amplitude is < 0 or > 0.9) throw new ArgumentOutOfRangeException(nameof(Amplitude));
        if (!double.IsFinite(NoiseAmplitude) || NoiseAmplitude is < 0 or > 0.09) throw new ArgumentOutOfRangeException(nameof(NoiseAmplitude));
        if (DropEvery < 0) throw new ArgumentOutOfRangeException(nameof(DropEvery));
        if (LeaseTimeoutMilliseconds is < 100 or > 60000) throw new ArgumentOutOfRangeException(nameof(LeaseTimeoutMilliseconds));
    }
}

public sealed record ReceiverState(int Ddc, bool Enabled, int Rate, double FrequencyHz);
public sealed record SimulatorState(bool Configured, bool Running, string? Client, long Discoveries,
    long AcceptedControls, long RejectedPackets, long UnsafeRequests, long IqPackets, long MicPackets,
    long StatusPackets, long InjectedDrops, long WatchdogStops, long Starts, long PacingResyncs,
    long SocketErrors, ReceiverState[] Receivers, TransmitState Transmit);

/// <summary>Virtual sink observations, never calibrated RF power. Sample metrics count keyed, in-order packets only.</summary>
public sealed record TransmitState(bool Enabled, bool Configured, bool Ptt, int Rate, double FrequencyHz,
    byte Drive, long Bursts, long Packets, long Samples, long UnkeyedPackets, long MissingPackets,
    long OutOfOrderPackets, long FullScaleComponents, double PeakMagnitude, double RmsMagnitude,
    double MeanI, double MeanQ, uint? LastSequence);
