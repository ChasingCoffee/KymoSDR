using System.Security.Cryptography;
using System.Text.Json;
using Thetis.Engine;

namespace Thetis.Preview;

// Deliberate allow-list: no audio device, mute, AF level, connection, TX,
// executable/native paths or legacy calibration/profile state can be restored.
public sealed record ReceiverPreferences(int Protocol = 2,int FrequencyHz = 14_199_000,
    ReceiveMode Mode = ReceiveMode.Usb,int LowCutHz = 300,int HighCutHz = 3000,
    ReceiveAgcMode AgcMode = ReceiveAgcMode.Medium,int AgcMaxGainDb = 60)
{
    public PreviewSettings ToSettings() => new(FrequencyHz,Mode,LowCutHz,HighCutHz,-40,true,AgcMode,AgcMaxGainDb);
    public static ReceiverPreferences From(int protocol,PreviewSettings settings) =>
        new(protocol,settings.FrequencyHz,settings.Mode,settings.LowCutHz,settings.HighCutHz,settings.AgcMode,settings.AgcMaxGainDb);
}
public sealed record WindowPreferences(double Width = 1140,double Height = 800,bool Maximized = false);
public sealed record PreviewPreferences(int SchemaVersion,ReceiverPreferences Receiver,WindowPreferences Window)
{
    public static PreviewPreferences Default => new(1,new(),new());
    public void Validate()
    {
        if (SchemaVersion != 1 || Receiver is null || Window is null) throw new ArgumentException("Unsupported or incomplete preferences.");
        if (Receiver.Protocol is not (1 or 2)) throw new ArgumentException("Unsupported simulator protocol.");
        Receiver.ToSettings().Validate();
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
            if (!document.RootElement.TryGetProperty("schemaVersion",out var version) || !version.TryGetInt32(out int schema) || schema != 1)
                throw new ArgumentException("Unknown settings version.");
            var value = JsonSerializer.Deserialize<PreviewPreferences>(bytes,AtomicJsonFile.Options) ?? throw new ArgumentException("Empty settings.");
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
}
