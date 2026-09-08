using System.Net;
using System.Net.Sockets;

namespace Thetis.Engine;

public sealed record P2ReceiveOptions(int BasePort, int Ddc = 2, int InputRate = 192000,
    int FrequencyHz = 14_199_000, string Address = "127.0.0.1", ReceiveDemodulation? Demodulation = null,
    ReceiveGain? Gain = null)
{
    internal void Validate(bool p1 = false)
    {
        if (BasePort < 1024 || BasePort > (p1 ? 65535 : 65515)) throw new ArgumentOutOfRangeException(nameof(BasePort));
        if (Ddc is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(Ddc));
        if (InputRate is not (48000 or 96000 or 192000 or 384000)) throw new ArgumentOutOfRangeException(nameof(InputRate));
        ValidateFrequency(FrequencyHz);
        Demodulation?.Validate();
        Gain?.Validate();
        if (!IPAddress.TryParse(Address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.IsLoopback(ip) || ip.ToString() != Address)
            throw new ArgumentException("Receive integration currently accepts canonical IPv4 loopback addresses only.");
    }
    internal static void ValidateFrequency(int frequency)
    { if (frequency is < 0 or > 61_440_000) throw new ArgumentOutOfRangeException(nameof(frequency)); }
}

/// <summary>Loopback-only P2 receive. No hardware or TX operation.</summary>
public sealed class P2ReceiveSession : ReceiveSession
{
    private P2ReceiveSession() { }
    public static P2ReceiveSession Open(string nativeDirectory, P2ReceiveOptions options,
        CancellationToken cancellationToken = default) => OpenCore(nativeDirectory, options, cancellationToken, null);
    internal static P2ReceiveSession OpenCore(string directory, P2ReceiveOptions options,
        CancellationToken token, Func<int, int>? checkpoint) =>
        OpenSession(directory, options, 2, token, checkpoint, () => new P2ReceiveSession());
}
