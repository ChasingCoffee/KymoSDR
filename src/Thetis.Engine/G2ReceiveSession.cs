using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Thetis.Engine;

/// <summary>Explicit hardware RX. Caller must first discover and verify an idle Saturn
/// on this interface. No automatic discovery, hostname resolution or interface fallback.</summary>
public sealed record G2ReceiveOptions(string RadioAddress, string LocalAddress, string SubnetMask,
    int FrequencyHz = 14_200_000, int DurationSeconds = 10, bool ConfirmExtendedReceive = false)
{
    public void Validate()
    {
        uint remote = Address(RadioAddress), local = Address(LocalAddress), mask = Address(SubnetMask, true);
        uint hosts = ~mask;
        if (mask == 0 || hosts < 3 || (hosts & unchecked(hosts + 1)) != 0 || remote == local ||
            (remote & mask) != (local & mask) || (remote & hosts) == 0 || (remote & hosts) == hosts ||
            (local & hosts) == 0 || (local & hosts) == hosts)
            throw new ArgumentException("G2 RX requires distinct unicast addresses on one explicit IPv4 subnet (/1../30).");
        ValidateFrequency(FrequencyHz);
        int maximum = ConfirmExtendedReceive ? 3600 : 60;
        if (DurationSeconds < 1 || DurationSeconds > maximum)
            throw new ArgumentException($"G2 RX duration must be 1..{maximum} seconds; extended runs require explicit opt-in.");
    }
    private static uint Address(string text, bool mask = false)
    {
        if (!IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != text)
            throw new ArgumentException("G2 RX requires canonical IPv4 literals, not hostnames.");
        uint value = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        if (!mask && (value >> 24 is 0 or 127 or >= 224))
            throw new ArgumentException("G2 RX requires non-loopback unicast endpoints.");
        return value;
    }
    internal static void ValidateFrequency(int frequency)
    {
        if (frequency is < 14_000_000 or > 14_350_000)
            throw new ArgumentException("Initial ANT1 receive profile is restricted to 14.000..14.350 MHz.");
    }
}

public sealed record G2ReceiveSafetyState(bool WorkerRunning, int StopReason, long KeyBitsSeen,
    long AdcOverloadBitsSeen, long StopDatagramsSent, long PolicyRejections);

/// <summary>Bounded ANT1/ADC0/DDC2/192k hardware receive. No TX or device output API.
/// PA-disable is a protocol request, not a physical RF interlock.</summary>
public sealed class G2ReceiveSession : ReceiveSession
{
    private G2ReceiveSession() : base(false) { }
    public static G2ReceiveSession Open(string nativeDirectory, G2ReceiveOptions options, CancellationToken token = default) =>
        OpenHardwareSession(nativeDirectory, options, token, () => new G2ReceiveSession());

    public G2ReceiveSafetyState Safety
    {
        get
        {
            lock (DspRuntime.Gate)
            {
                _ = State; // validate lifetime and retain this session's SafeHandle
                long[] s = new long[8];
                if (G2ReceiveNative.ThetisG2ReceiveGetState(s, s.Length) != 8 || s[0] != 1 || s[1] != 1)
                    throw new NotSupportedException("Native G2 safety state is incompatible.");
                GC.KeepAlive(this);
                return new(s[2] != 0, checked((int)s[3]), s[4], s[5], s[6], s[7]);
            }
        }
    }
}

internal static class G2ReceiveNative
{
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisG2ReceiveAbi();
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisG2ReceiveOpen(int abi,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string remote, [MarshalAs(UnmanagedType.LPUTF8Str)] string local,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mask, int frequency, int seconds,
        ChannelMasterNative.Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisG2ReceiveEnduranceOpen(int abi,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string remote, [MarshalAs(UnmanagedType.LPUTF8Str)] string local,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mask, int frequency, int seconds,
        ChannelMasterNative.Checkpoint checkpoint, nint context);
    [DllImport(NativeMethods.Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ThetisG2ReceiveGetState([Out] long[] values, int capacity);
}
