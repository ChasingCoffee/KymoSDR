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

    internal static void Write(string path,byte[] bytes,CancellationToken token = default,Action? beforePublish = null)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Choose an absolute file path.",nameof(path));
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); beforePublish?.Invoke();
            File.Move(temporary,path,overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
