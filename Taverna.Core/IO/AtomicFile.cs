using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Taverna.Core.IO;

public static class AtomicFile
{
    /// <summary>
    /// Writes to a sibling temp file, flushes it to disk, then renames it over the target, so a crash
    /// leaves either the old or the new content, never a torn file.
    /// </summary>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, target, overwrite: true);
                    return;
                }
                // Indexers and antivirus scanners briefly lock freshly written files on Windows.
                catch (IOException) when (attempt < 4) { await Task.Delay(50 * (attempt + 1), ct); }
                catch (UnauthorizedAccessException) when (attempt < 4) { await Task.Delay(50 * (attempt + 1), ct); }
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

public static class TavernaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Unreadable state files are kept aside instead of being overwritten.</summary>
    public static void QuarantineCorrupt(string path)
    {
        try { File.Move(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false); }
        catch (IOException) { }
    }
}
