using System.Text.Json;
using System.Text.Json.Serialization;

namespace Thetis.Preview;

/// <summary>Control-thread persistence only. Never used by a radio/audio callback.</summary>
public static class AtomicJsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static Task WriteAsync<T>(string path,T value,CancellationToken token = default) =>
        Task.Run(() => Write(path,JsonSerializer.SerializeToUtf8Bytes(value,Options),token),token);

    internal static void Write(string path,byte[] bytes,CancellationToken token = default,Action? beforePublish = null,string? temporaryFileName = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute file path.",nameof(path));
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string name = temporaryFileName ?? $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp";
        if (Path.GetFileName(name) != name) throw new ArgumentException("Temporary name must stay in the destination directory.",nameof(temporaryFileName));
        string temporary = Path.Combine(directory,name);
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { created = true; stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); beforePublish?.Invoke();
            File.Move(temporary,path,overwrite: true);
        }
        finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
    }
}
