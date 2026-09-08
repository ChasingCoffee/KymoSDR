using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Thetis.Audio;

public sealed record PlaybackDevice(int Index, string Name, string HostApi, int DefaultRate, int SupportedRates)
{
    public int PreferredRate => (SupportedRates & 2) != 0 ? 48000 : (SupportedRates & 1) != 0 ? 44100 : 96000;
    public override string ToString() => $"{Name} ({HostApi})";
}
public sealed record PlaybackState(bool Physical, bool Active, int Rate, bool Muted, long Queued,
    long Submitted, long Rejected, long Rendered, long StarvationFrames, long Underruns,
    long DriverUnderruns, long NonfiniteSamples, long ClippedSamples, double Peak);

/// <summary>One output-only native owner. No-device output never initializes a hardware backend.
/// Device callbacks remain entirely native; Write accepts stereo 48 kHz PCM.</summary>
public sealed class PlaybackOutput : IDisposable
{
    private static readonly object Gate = new();
    private static nint library;
    private static string? loadedPath;
    private static bool active;
    private readonly OutputHandle handle = new();
    private PlaybackOutput() { }
    static PlaybackOutput() => NativeLibrary.SetDllImportResolver(typeof(PlaybackOutput).Assembly, Resolve);
    private static nint Resolve(string name, Assembly _, DllImportSearchPath? __) => name == "thetis_audio"
        ? library != 0 ? library : throw new InvalidOperationException("Load playback from an explicit native directory first.") : 0;
    private static void Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Native directory must be absolute.");
        string filename = OperatingSystem.IsWindows() ? "thetis_audio.dll" : OperatingSystem.IsMacOS() ? "libthetis_audio.dylib" : "libthetis_audio.so";
        string path = Path.Combine(Path.GetFullPath(directory),filename);
        lock (Gate)
        {
            if (loadedPath is not null && loadedPath != path) throw new InvalidOperationException("Playback is already loaded from a different directory.");
            if (library == 0) { library = NativeLibrary.Load(path); loadedPath = path; }
            if (AudioNative.ThetisAudioAbi() != 1) throw new NotSupportedException("Native playback ABI is incompatible.");
        }
    }
    public static IReadOnlyList<PlaybackDevice> EnumerateDevices(string directory)
    {
        Load(directory);
        return AudioControl.Invoke(() =>
        {
            lock (Gate)
            {
                if (active) throw new InvalidOperationException("Disconnect playback before refreshing devices.");
                Check(AudioNative.ThetisAudioInitialize());
                try
                {
                    int count = AudioNative.ThetisAudioDeviceCount(); Check(count);
                    List<PlaybackDevice> devices = [];
                    for (int index = 0; index < count; ++index)
                    {
                        int[] values = new int[5]; byte[] name = new byte[1024], host = new byte[256];
                        int rc = AudioNative.ThetisAudioDevice(index,values,5,name,name.Length,host,host.Length); Check(rc);
                        if (rc != 5 || values[0] != 1) throw new NotSupportedException("Native audio device layout is incompatible.");
                        if (values[2] >= 2 && values[4] != 0) devices.Add(new(index,Decode(name),Decode(host),values[3],values[4]));
                    }
                    return (IReadOnlyList<PlaybackDevice>)devices.AsReadOnly();
                }
                finally { Check(AudioNative.ThetisAudioTerminate()); }
            }
        });
    }
    public static PlaybackOutput OpenNull(string directory,int rate = 48000) => Open(directory,null,rate);
    public static PlaybackOutput OpenDevice(string directory,PlaybackDevice device)
    { ArgumentNullException.ThrowIfNull(device); return Open(directory,device,device.PreferredRate); }
    private static PlaybackOutput Open(string directory,PlaybackDevice? device,int rate)
    {
        if (rate is not (44100 or 48000 or 96000)) throw new ArgumentOutOfRangeException(nameof(rate));
        if (device is not null && (device.Index < 0 || string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.HostApi)))
            throw new ArgumentException("Choose an enumerated output device.");
        Load(directory);
        return AudioControl.Invoke(() =>
        {
            lock (Gate)
            {
                if (active) throw new InvalidOperationException("One playback output may be open at a time.");
                var result = new PlaybackOutput();
                Check(AudioNative.ThetisAudioOpen(1,device?.Index ?? -1,rate,device?.Name,device?.HostApi));
                result.handle.MarkOpen(); active = true; return result;
            }
        });
    }
    public PlaybackState State
    {
        get
        {
            lock (Gate)
            {
                CheckOpen(); long[] s = new long[16]; int rc = AudioNative.ThetisAudioState(s,s.Length); GC.KeepAlive(handle); Check(rc);
                if (rc != 16 || s[0] != 1 || s[1] != 1) throw new NotSupportedException("Native playback state is incompatible.");
                return new(s[2] == 1,s[3] == 1,(int)s[4],s[5] == 1,s[6],s[7],s[8],s[9],s[10],s[11],s[12],s[13],s[14],s[15]/1e6);
            }
        }
    }
    public int Write(double[] stereo,int frames)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        if (frames < 1 || frames > 16384 || frames > stereo.Length/2) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (Gate) { CheckOpen(); int rc = AudioNative.ThetisAudioWrite(stereo,frames); GC.KeepAlive(handle); Check(rc); return rc; }
    }
    public void SetMuted(bool muted)
    { lock (Gate) { CheckOpen(); Check(AudioNative.ThetisAudioMute(muted ? 1 : 0)); GC.KeepAlive(handle); } }
    /// <summary>Requests queue discard at the consumer boundary. Driver-buffered audio cannot be recalled.</summary>
    public void Flush()
    { lock (Gate) { CheckOpen(); Check(AudioNative.ThetisAudioFlush()); GC.KeepAlive(handle); } }
    public int RenderNull(float[] stereo,int frames)
    {
        ArgumentNullException.ThrowIfNull(stereo);
        if (frames < 1 || frames > 4096 || frames > stereo.Length/2) throw new ArgumentOutOfRangeException(nameof(frames));
        lock (Gate) { CheckOpen(); int rc = AudioNative.ThetisAudioRenderNull(stereo,frames); GC.KeepAlive(handle); Check(rc); return rc; }
    }
    internal void InterruptNull()
    { lock (Gate) { CheckOpen(); Check(AudioNative.ThetisAudioInterruptNull()); GC.KeepAlive(handle); } }
    private void CheckOpen() => ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid,this);
    private static string Decode(byte[] text) => Encoding.UTF8.GetString(text,0,Array.IndexOf(text,(byte)0));
    private static void Check(int code)
    {
        if (code >= 0) return;
        byte[] message = new byte[1024];
        string detail = AudioNative.ThetisAudioError(code,message,message.Length) == 0 ? Decode(message) : "Unknown native audio error";
        throw new IOException($"{detail} ({code}).");
    }
    public void Dispose() { handle.Dispose(); Check(handle.CloseError); }
    private sealed class OutputHandle() : SafeHandle(0,true)
    {
        internal int CloseError { get; private set; }
        public override bool IsInvalid => handle == 0;
        internal void MarkOpen() => SetHandle(1);
        protected override bool ReleaseHandle() => AudioControl.Invoke(() =>
        {
            lock (Gate)
            {
                int rc = AudioNative.ThetisAudioClose();
                CloseError = rc;
                if (rc == 0) active = false;
                return rc == 0;
            }
        });
    }
}

// PortAudio initialization/termination (including WASAPI COM ownership) occur
// on one stable thread. Never used by the real-time callback or PCM producer.
internal static class AudioControl
{
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly Thread Worker = Start();
    private static Thread Start()
    {
        var thread = new Thread(() => { foreach (var work in Work.GetConsumingEnumerable()) work(); })
        { IsBackground = true, Name = "Kymo audio control" };
        thread.Start(); return thread;
    }
    internal static T Invoke<T>(Func<T> work)
    {
        if (Thread.CurrentThread == Worker) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Work.Add(() => { try { completion.SetResult(work()); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task.GetAwaiter().GetResult();
    }
}
internal static class AudioNative
{
    private const string Library = "thetis_audio";
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioAbi();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioInitialize();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioTerminate();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioDeviceCount();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioDevice(int index,[Out] int[] values,int capacity,[Out] byte[] name,int nameCapacity,[Out] byte[] host,int hostCapacity);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioOpen(int abi,int device,int rate,[MarshalAs(UnmanagedType.LPUTF8Str)] string? name,[MarshalAs(UnmanagedType.LPUTF8Str)] string? host);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioWrite([In] double[] stereo,int frames);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioMute(int muted);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioFlush();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioRenderNull([Out] float[] stereo,int frames);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioInterruptNull();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioState([Out] long[] values,int capacity);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioClose();
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)] internal static extern int ThetisAudioError(int code,[Out] byte[] text,int capacity);
}
