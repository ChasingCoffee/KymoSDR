// SPDX-License-Identifier: GPL-2.0-or-later
// New synthetic test peer, not a port of FPGA/DMA or radio control code.
// Wire references and the deliberately limited profile are in docs/G2_SIMULATOR.md.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Thetis.Simulator;

internal sealed class P2Device
{
    internal const int DdcCount = 10, SamplesPerPacket = 238, IqPacketSize = 1444;
    internal static readonly int[] SocketOffsets = [0, 1, 2, 3, 4, 5, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
    private static readonly (int Field, int Offset)[] PortFields = [(5, 1), (7, 2), (9, 3), (11, 1), (13, 4), (15, 5), (17, 11), (19, 2)];
    private readonly SimulatorOptions options;
    private readonly Ddc[] receivers = Enumerable.Range(0, DdcCount).Select(_ => new Ddc()).ToArray();
    private readonly byte[] mic = new byte[132], status = new byte[60];
    private IPEndPoint? client;
    private bool running, receiverConfigured, phaseWords;
    private double lastControl, micDue, statusDue;
    private uint micSequence, statusSequence;
    private long discoveries, controls, rejected, unsafeRequests, iqPackets, micPackets, statusPackets;
    private long injectedDrops, watchdogStops, starts, pacingResyncs, socketErrors;

    internal P2Device(SimulatorOptions options)
    {
        options.Validate();
        if (options.BasePort == 0) throw new ArgumentException("Resolve the bound base port first.");
        this.options = options;
    }

    internal SimulatorState Snapshot() => new(client is not null, running, client?.ToString(), discoveries,
        controls, rejected, unsafeRequests, iqPackets, micPackets, statusPackets, injectedDrops, watchdogStops,
        starts, pacingResyncs, socketErrors, receivers.Select((r, i) => new ReceiverState(i, r.Enabled, r.Rate, r.Frequency)).ToArray());
    internal bool Running => running;
    internal void SocketFailure() { ++socketErrors; Release(); }
    internal void Shutdown() => Release();
    private void Release()
    {
        client = null; running = receiverConfigured = false;
        foreach (var receiver in receivers) receiver.Enabled = false;
    }
    private void Expire(double now)
    {
        // Always bounded, even if the client disables the hardware watchdog.
        if (client is not null && now - lastControl >= options.LeaseTimeoutMilliseconds / 1000.0)
        { ++watchdogStops; Release(); }
    }

    internal void Accept(int offset, ReadOnlySpan<byte> packet, IPEndPoint source, double now,
        Action<int, byte[], IPEndPoint> send)
    {
        Expire(now);
        if (source.AddressFamily != AddressFamily.InterNetwork || !IPAddress.IsLoopback(source.Address) ||
            SocketOffsets.Contains(source.Port - options.BasePort)) { ++rejected; return; }
        if (offset == 0 && packet.Length == 60 && packet[..4].IndexOfAnyExcept((byte)0) < 0 && packet[4] == 2)
        {
            byte[] reply = new byte[60];
            reply[4] = running ? (byte)3 : (byte)2;
            // Distinct locally administered simulator identity, not the user's MAC.
            new byte[] { 2, 0x4b, 0x59, 0x4d, 0x4f, 1 }.CopyTo(reply, 5);
            reply[11] = 10; reply[12] = 43; reply[13] = 27;
            reply[20] = DdcCount; reply[21] = 1;
            ++discoveries; send(0, reply, source); return;
        }
        // Protect an active stream. An idle device advertises available, so a
        // fresh general packet may claim it immediately after a stop.
        if (client is not null && !client.Equals(source) && (running || offset != 0)) { ++rejected; return; }
        if (offset == 0)
        {
            if (!ValidGeneral(packet)) { ++rejected; return; }
            bool newClient = client is null || !client.Equals(source);
            client = new IPEndPoint(source.Address, source.Port);
            phaseWords = (packet[37] & 8) != 0;
            if (newClient)
            {
                receiverConfigured = false;
                foreach (var receiver in receivers) receiver.Enabled = false;
            }
            lastControl = now; ++controls; return;
        }
        if (client is null) { ++rejected; return; }
        if (offset == 1)
        {
            if (!ValidReceivers(packet)) { ++rejected; return; }
            int enabled = BinaryPrimitives.ReadUInt16LittleEndian(packet[7..]);
            for (int i = 0; i < DdcCount; ++i)
            {
                var r = receivers[i];
                bool nextEnabled = (enabled & (1 << i)) != 0;
                int rate = nextEnabled ? BinaryPrimitives.ReadUInt16BigEndian(packet[(18 + 6 * i)..]) * 1000 : r.Rate;
                if (nextEnabled != r.Enabled || rate != r.Rate) r.Due = now;
                r.Enabled = nextEnabled; r.Rate = rate;
            }
            receiverConfigured = true; lastControl = now; ++controls; return;
        }
        if (offset == 3)
        {
            if (packet.Length != IqPacketSize) { ++rejected; return; }
            if ((packet[4] & 0xfe) != 0 || (packet[5] & 7) != 0)
            { ++unsafeRequests; ++rejected; Release(); return; }
            bool nextRunning = (packet[4] & 1) != 0;
            if (nextRunning && !receiverConfigured) { ++rejected; return; }
            for (int i = 0; i < DdcCount; ++i)
            {
                uint word = BinaryPrimitives.ReadUInt32BigEndian(packet[(9 + i * 4)..]);
                receivers[i].Frequency = phaseWords ? word * (122_880_000.0 / 4_294_967_296.0) : word;
            }
            if (nextRunning && !running)
            {
                ++starts;
                for (int i = 0; i < DdcCount; ++i) receivers[i].Restart(now, options.Seed, i);
                micDue = statusDue = now; micSequence = statusSequence = 0;
            }
            running = nextRunning; lastControl = now; ++controls; return;
        }
        // Receive clients can send these setup/audio packets; no device or TX
        // processing is attached. They deliberately do not keep a lease alive.
        if ((offset == 2 && packet.Length == 60) || (offset == 4 && packet.Length == 260)) return;
        if (offset == 5) { ++unsafeRequests; Release(); }
        ++rejected;
    }

    private bool ValidGeneral(ReadOnlySpan<byte> p)
    {
        if (p.Length != 60 || p[4] != 0 || (p[37] & ~8) != 0 || p[23] != 0) return false;
        foreach (var (field, offset) in PortFields)
        {
            int port = BinaryPrimitives.ReadUInt16BigEndian(p[field..]);
            // Zero means protocol default, NOT a relative port. Custom layouts
            // must be explicitly advertised by the client; no silent remapping.
            if (port != options.BasePort + offset && !(port == 0 && options.BasePort == 1024)) return false;
        }
        return true;
    }
    private static bool ValidReceivers(ReadOnlySpan<byte> p)
    {
        if (p.Length != IqPacketSize || p[4] is < 1 or > 2 ||
            p.Slice(9, 8).IndexOfAnyExcept((byte)0) >= 0 ||
            p[1363..].IndexOfAnyExcept((byte)0) >= 0) return false; // no synchronized/interleaved DDCs
        int enabled = BinaryPrimitives.ReadUInt16LittleEndian(p[7..]);
        if ((enabled & ~0x3ff) != 0) return false;
        for (int i = 0; i < DdcCount; ++i)
        {
            if ((enabled & (1 << i)) == 0) continue;
            int field = 17 + 6 * i;
            if (p[field] > 1 || p[field + 5] != 24 ||
                BinaryPrimitives.ReadUInt16BigEndian(p[(field + 1)..]) is not (48 or 96 or 192 or 384)) return false;
        }
        return true;
    }

    internal void Tick(double now, Action<int, byte[], IPEndPoint> send)
    {
        Expire(now);
        if (!running || client is null) return;
        for (int i = 0; i < DdcCount && running; ++i)
        {
            var r = receivers[i];
            if (!r.Enabled) continue;
            double interval = SamplesPerPacket / (double)r.Rate;
            int batch = 0;
            while (r.Due <= now && batch++ < 32 && running)
            {
                FillIq(r);
                ++r.Ordinal;
                if (options.DropEvery > 0 && r.Ordinal % (ulong)options.DropEvery == 0) ++injectedDrops;
                else { send(11 + i, r.Packet, client!); ++iqPackets; }
                r.Due += interval;
            }
            if (r.Due <= now) { r.Due = now + interval; ++pacingResyncs; }
        }
        int micBatch = 0;
        while (running && micDue <= now && micBatch++ < 32)
        {
            BinaryPrimitives.WriteUInt32BigEndian(mic, micSequence++);
            send(2, mic, client!); ++micPackets; micDue += 64.0 / 48000;
        }
        if (micDue <= now) { micDue = now + 64.0 / 48000; ++pacingResyncs; }
        if (running && statusDue <= now)
        {
            BinaryPrimitives.WriteUInt32BigEndian(status, statusSequence++);
            // All telemetry/PTT/CW/power fields stay zero; no fake RF readings.
            send(1, status, client!); ++statusPackets; statusDue = now + 0.2;
        }
    }

    private void FillIq(Ddc r)
    {
        BinaryPrimitives.WriteUInt32BigEndian(r.Packet, r.Sequence++);
        // Timestamp bytes 4..11 remain zero, matching the inspected p2app path.
        BinaryPrimitives.WriteUInt16BigEndian(r.Packet.AsSpan(12), 24);
        BinaryPrimitives.WriteUInt16BigEndian(r.Packet.AsSpan(14), SamplesPerPacket);
        double offset = options.ToneFrequencyHz - r.Frequency;
        double step = 2 * Math.PI * Math.IEEERemainder(offset, r.Rate) / r.Rate;
        double amplitude = Math.Abs(offset) < r.Rate / 2.0 ? options.Amplitude : 0;
        for (int i = 0; i < SamplesPerPacket; ++i)
        {
            Write24(r.Packet.AsSpan(16 + 6 * i), amplitude * Math.Cos(r.Phase) + Noise(r));
            Write24(r.Packet.AsSpan(19 + 6 * i), amplitude * Math.Sin(r.Phase) + Noise(r));
            r.Phase = Math.IEEERemainder(r.Phase + step, 2 * Math.PI);
        }
    }
    private double Noise(Ddc r)
    {
        if (options.NoiseAmplitude == 0) return 0;
        uint x = r.Noise;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5; r.Noise = x;
        return options.NoiseAmplitude * (x / (double)uint.MaxValue * 2 - 1);
    }
    internal static void Write24(Span<byte> target, double sample)
    {
        int value = (int)Math.Clamp(Math.Round(sample * 8388608), -8388608, 8388607);
        target[0] = (byte)(value >> 16); target[1] = (byte)(value >> 8); target[2] = (byte)value;
    }
    private sealed class Ddc
    {
        internal bool Enabled;
        internal int Rate = 48000;
        internal double Frequency, Phase, Due;
        internal uint Sequence, Noise;
        internal ulong Ordinal;
        internal readonly byte[] Packet = new byte[IqPacketSize];
        internal void Restart(double now, uint seed, int ddc)
        {
            Phase = 0; Sequence = 0; Ordinal = 0; Due = now;
            Noise = seed ^ unchecked(0x9e3779b9u * (uint)(ddc + 1));
            if (Noise == 0) Noise = 1;
        }
    }
}
