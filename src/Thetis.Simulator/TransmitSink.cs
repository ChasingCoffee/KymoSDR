// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;

namespace Thetis.Simulator;

/// <summary>Bounded, sample-discarding virtual TX load. No device, playback, forwarding or RF model.</summary>
internal sealed class TransmitSink(bool enabled)
{
    internal const int Rate = 192000, SamplesPerPacket = 240, PacketSize = 1444;
    internal bool Configured { get; private set; }
    internal bool Ptt { get; private set; }
    private double frequency, peakSquared, sumSquared, sumI, sumQ;
    private byte drive;
    private long bursts, packets, samples, unkeyed, missing, outOfOrder, fullScale;
    private uint? lastSequence;

    internal TransmitState Snapshot() => new(enabled, Configured, Ptt, Rate, frequency, drive,
        bursts, packets, samples, unkeyed, missing, outOfOrder, fullScale, Math.Sqrt(peakSquared),
        samples == 0 ? 0 : Math.Sqrt(sumSquared / samples), samples == 0 ? 0 : sumI / samples,
        samples == 0 ? 0 : sumQ / samples, lastSequence);

    internal bool Configure(ReadOnlySpan<byte> p)
    {
        // One fixed-rate DUC. Zero rate/width accepts the legacy/default profile.
        // CW and EER require waveform/keyer models that this sink does not implement.
        if (!enabled || p.Length != 60 || p[4] != 1 || (p[5] & 3) != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(p[14..]) is not (0 or 192) || p[16] is not (0 or 24)) return false;
        Configured = true;
        return true;
    }

    internal void Start()
    {
        Ptt = false;
        bursts = packets = samples = unkeyed = missing = outOfOrder = fullScale = 0;
        peakSquared = sumSquared = sumI = sumQ = 0;
        lastSequence = null;
    }
    internal void Control(bool ptt, double frequencyHz, byte driveLevel)
    {
        if (!enabled) return;
        if (ptt && !Ptt) ++bursts;
        Ptt = ptt; frequency = frequencyHz; drive = driveLevel;
    }
    internal void Release()
    {
        Ptt = Configured = false;
        frequency = 0; drive = 0;
        // Preserve the last run's aggregate diagnostics for the stopped event.
    }

    internal bool Accept(ReadOnlySpan<byte> p, bool running)
    {
        if (!enabled || !Configured || !running || p.Length != PacketSize) return false;
        uint sequence = BinaryPrimitives.ReadUInt32BigEndian(p);
        if (lastSequence is uint last)
        {
            uint distance = unchecked(sequence - last - 1);
            if (distance >= 0x80000000u) { ++outOfOrder; return true; }
            missing += distance;
        }
        lastSequence = sequence;
        if (!Ptt) { ++unkeyed; return true; }
        ++packets; samples += SamplesPerPacket;
        for (int n = 0; n < SamplesPerPacket; ++n)
        {
            int i = Read24(p[(4 + 6 * n)..]), q = Read24(p[(7 + 6 * n)..]);
            if (i is -8388608 or 8388607) ++fullScale;
            if (q is -8388608 or 8388607) ++fullScale;
            double real = i / 8388608.0, imaginary = q / 8388608.0;
            double magnitudeSquared = real * real + imaginary * imaginary;
            peakSquared = Math.Max(peakSquared, magnitudeSquared);
            sumSquared += magnitudeSquared; sumI += real; sumQ += imaginary;
        }
        return true;
    }
    private static int Read24(ReadOnlySpan<byte> p) => (p[0] << 24 | p[1] << 16 | p[2] << 8) >> 8;
}
