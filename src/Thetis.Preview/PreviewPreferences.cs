using System.Security.Cryptography;
using System.Text.Json;
using Thetis.Audio;
using Thetis.Engine;

namespace Thetis.Preview;

// Deliberate allow-list: selection bookmarks are not connection permission.
// No mute/AF level, connected state, consent, TX, executable/native paths or
// legacy calibration/profile state can be restored.
public sealed record ReceiverPreferences(int Protocol = 2,int FrequencyHz = 14_199_000,
    ReceiveMode Mode = ReceiveMode.Usb,int LowCutHz = 300,int HighCutHz = 3000,
    ReceiveAgcMode AgcMode = ReceiveAgcMode.Medium,int AgcMaxGainDb = 60)
{
    public PreviewSettings ToSettings() => new(FrequencyHz,Mode,LowCutHz,HighCutHz,-40,true,AgcMode,AgcMaxGainDb);
    public static ReceiverPreferences From(int protocol,PreviewSettings settings) =>
        new(protocol,settings.FrequencyHz,settings.Mode,settings.LowCutHz,settings.HighCutHz,settings.AgcMode,settings.AgcMaxGainDb);
}
public sealed record WindowPreferences(double Width = 1140,double Height = 800,bool Maximized = false);
public sealed record ConnectionPreferences(G2RadioTarget? LastG2 = null,int DurationSeconds = 60,bool UseG2 = false)
{
    public void Validate()
    {
        LastG2?.Validate();
        if (DurationSeconds is < 5 or > 60 || (UseG2 && LastG2 is null)) throw new ArgumentException("Invalid saved G2 selection.");
    }
}
public sealed record AudioOutputPreferences(string Name,string HostApi,int OutputChannels,int FirstChannel,string LeftName,string RightName)
{
    public void Validate()
    {
        if (new[] {Name,HostApi,LeftName,RightName}.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1024 || s.Contains('\0')) ||
            OutputChannels is < 2 or > 128 || FirstChannel < 0 || FirstChannel%2 != 0 || FirstChannel > OutputChannels-2)
            throw new ArgumentException("Invalid saved audio output selection.");
    }
    public static AudioOutputPreferences From(PlaybackDevice device,PlaybackPair pair) =>
        new(device.Name,device.HostApi,device.OutputChannels,pair.FirstChannel,pair.LeftName,pair.RightName);
    public PlaybackDevice? Match(IReadOnlyList<PlaybackDevice> devices)
    {
        Validate();
        var matching = devices.Where(d => d.Name == Name && d.HostApi == HostApi).ToArray();
        if (matching.Length != 1 || matching[0].OutputChannels != OutputChannels) return null;
        var pair = matching[0].Pairs.SingleOrDefault(p => p.FirstChannel == FirstChannel && p.LeftName == LeftName &&
            p.RightName == RightName && p.SupportedRates != 0);
        return pair is null ? null : matching[0].WithPair(pair);
    }
}
public sealed record PreviewPreferences(int SchemaVersion,ReceiverPreferences Receiver,WindowPreferences Window,
    ConnectionPreferences? Connection = null,AudioOutputPreferences? Audio = null,bool AudioSelectionInitialized = false)
{
    public static PreviewPreferences Default => new(2,new(),new());
    public void Validate()
    {
        if (SchemaVersion != 2 || Receiver is null || Window is null) throw new ArgumentException("Unsupported or incomplete preferences.");
        if (Receiver.Protocol is not (1 or 2)) throw new ArgumentException("Unsupported simulator protocol.");
        Receiver.ToSettings().Validate();
        Connection?.Validate(); Audio?.Validate();
        if (!double.IsFinite(Window.Width) || !double.IsFinite(Window.Height) ||
            Window.Width is < 900 or > 3840 || Window.Height is < 700 or > 2160)
            throw new ArgumentException("Window size is outside supported bounds.");
    }
}
public sealed record PreferencesLoadResult(PreviewPreferences Value,string? Warning,bool CanSave);

/// <summary>One store per window. Invalid/future/external edits are preserved, not overwritten.</summary>
public sealed class PreviewPreferencesStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"KymoSDR","preview-settings.json");
    private const int MaxBytes = 64*1024;
    private readonly SemaphoreSlim gate = new(1,1);
    private string? fingerprint;
    private bool loaded,canSave;
    public string PathName { get; } = Path.IsPathFullyQualified(path) ? path : throw new ArgumentException("Settings path must be absolute.",nameof(path));

    public PreferencesLoadResult Load()
    {
        gate.Wait();
        try
        {
            loaded = true; canSave = false;
            byte[]? bytes = ReadBounded(); fingerprint = Hash(bytes);
            if (bytes is null) { canSave = true; return new(PreviewPreferences.Default,null,true); }
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("schemaVersion",out var version) || !version.TryGetInt32(out int schema) || schema is not (1 or 2))
                throw new ArgumentException("Unknown settings version.");
            // Schema 1 has its own strict shape: do not accept selection fields
            // disguised as the old schema. Migration is saved only on a normal save.
            var value = schema == 1
                ? (JsonSerializer.Deserialize<LegacyPreferences>(bytes,AtomicJsonFile.Options) ?? throw new ArgumentException("Empty settings.")).Upgrade()
                : JsonSerializer.Deserialize<PreviewPreferences>(bytes,AtomicJsonFile.Options) ?? throw new ArgumentException("Empty settings.");
            // An old saved No-device selection must not become system speakers
            // merely because this additive setting did not exist in that build.
            if (!document.RootElement.TryGetProperty("audioSelectionInitialized",out _))
                value = value with { AudioSelectionInitialized = true };
            value.Validate(); canSave = true; return new(value,null,true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { return new(PreviewPreferences.Default,"Settings could not be restored. Safe defaults are in use; the existing file will not be overwritten.",false); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(PreviewPreferences value,CancellationToken token = default)
    {
        value.Validate(); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!loaded || !canSave) throw new InvalidOperationException("Settings are protected after an unsuccessful load. Preserve or move the file before restarting.");
            await Task.Run(() =>
            {
                if (Hash(ReadBounded()) != fingerprint)
                { canSave = false; throw new IOException("Settings changed outside this window; they were not overwritten. Restart to reload them."); }
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value,AtomicJsonFile.Options);
                AtomicJsonFile.Write(PathName,bytes,token); fingerprint = Hash(bytes);
            },token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private byte[]? ReadBounded()
    {
        try
        {
            using var stream = new FileStream(PathName,FileMode.Open,FileAccess.Read,FileShare.Read);
            if (stream.Length > MaxBytes) throw new IOException("Settings exceed 64 KiB.");
            using var copy = new MemoryStream();
            byte[] buffer = new byte[4096]; int count;
            while ((count = stream.Read(buffer)) > 0)
            { if (copy.Length+count > MaxBytes) throw new IOException("Settings exceed 64 KiB."); copy.Write(buffer,0,count); }
            return copy.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string? Hash(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record LegacyPreferences(int SchemaVersion,ReceiverPreferences Receiver,WindowPreferences Window)
    {
        public PreviewPreferences Upgrade() => new(2,Receiver,Window);
    }
}
