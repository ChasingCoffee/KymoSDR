namespace Thetis.Engine;

public sealed record P1ReceiveOptions(int Port, int FrequencyHz = 14_199_000,
    string Address = "127.0.0.1", ReceiveDemodulation? Demodulation = null, ReceiveGain? Gain = null);

/// <summary>Loopback-only P1 UDP receive: one receiver, 48 kHz, no hardware or TX operation.</summary>
public sealed class P1ReceiveSession : ReceiveSession
{
    private P1ReceiveSession() { }
    public static P1ReceiveSession Open(string nativeDirectory, P1ReceiveOptions options,
        CancellationToken cancellationToken = default) => OpenCore(nativeDirectory, options, cancellationToken, null);
    internal static P1ReceiveSession OpenCore(string directory, P1ReceiveOptions options,
        CancellationToken token, Func<int, int>? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenSession(directory, new(options.Port, 0, 48000, options.FrequencyHz, options.Address, options.Demodulation, options.Gain),
            1, token, checkpoint, () => new P1ReceiveSession());
    }
}
