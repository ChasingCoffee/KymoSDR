namespace Thetis.Engine;

/// <summary>Opt-in bounded mono PCM capture for offline receive analysis. No file I/O
/// on the PCM pump. Capture normalization never changes the speaker signal.</summary>
public sealed class ReceiveAudioCapture
{
    private readonly double[] mono;
    private readonly DateTimeOffset startUtc;
    private int count;
    private bool frozen;
    public int Frames => Volatile.Read(ref count);
    public DateTimeOffset? FirstSampleObservedUtc { get; private set; }
    public DateTimeOffset RequestedStartUtc => startUtc;
    public ReceiveAudioCapture(int seconds,DateTimeOffset startUtc)
    {
        if (seconds is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(seconds));
        mono = new double[48000*seconds]; this.startUtc = startUtc;
    }
    internal void Add(double[] stereo,int frames)
    {
        if (frozen || count == mono.Length || DateTimeOffset.UtcNow < startUtc) return;
        FirstSampleObservedUtc ??= DateTimeOffset.UtcNow;
        int take = Math.Min(frames,mono.Length-count);
        for (int i = 0; i < take; ++i)
        {
            double value = stereo[2*i];
            if (!double.IsFinite(value)) throw new InvalidDataException("Non-finite receive capture.");
            mono[count+i] = value;
        }
        Volatile.Write(ref count,count+take);
    }
    internal void Freeze() => Volatile.Write(ref frozen,true);
    public void SaveWave(string path)
    {
        if (!Volatile.Read(ref frozen)) throw new InvalidOperationException("Stop the receiver before exporting its capture.");
        if (!Path.IsPathFullyQualified(path) || count == 0) throw new ArgumentException("An absolute new WAV path and received samples are required.");
        double peak = 0;
        for (int i = 0; i < count; ++i) peak = Math.Max(peak,Math.Abs(mono[i]));
        double scale = peak == 0 ? 0 : 16383/peak; // offline normalization to approximately -6 dBFS
        using var stream = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36+count*2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(count*2);
        for (int i = 0; i < count; ++i) writer.Write((short)Math.Clamp(Math.Round(mono[i]*scale),short.MinValue,short.MaxValue));
    }
}
