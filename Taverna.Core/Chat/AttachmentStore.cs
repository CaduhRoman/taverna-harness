namespace Taverna.Core.Chat;

/// <summary>Stores chat images under .taverna/attachments/ and returns paths relative to the project root.</summary>
public static class AttachmentStore
{
    public static readonly IReadOnlySet<string> ImageExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    public const long MaxImageBytes = 10 * 1024 * 1024;

    public static string Directory(string projectRoot) => Path.Combine(projectRoot, ".taverna", "attachments");

    public static async Task<string> SaveAsync(string projectRoot, byte[] data, string extension, CancellationToken ct = default)
    {
        extension = extension.StartsWith('.') ? extension : "." + extension;
        if (!ImageExtensions.Contains(extension)) throw new ArgumentException($"Unsupported image type: {extension}");
        if (data.Length == 0) throw new ArgumentException("Image is empty.");
        if (data.Length > MaxImageBytes) throw new ArgumentException("Image is larger than 10 MB.");

        var folder = Directory(projectRoot);
        System.IO.Directory.CreateDirectory(folder);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{extension.ToLowerInvariant()}";
        await File.WriteAllBytesAsync(Path.Combine(folder, name), data, ct);
        return Path.Combine(".taverna", "attachments", name);
    }

    public static async Task<string> ImportAsync(string projectRoot, string sourcePath, CancellationToken ct = default)
    {
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxImageBytes) throw new ArgumentException("Image is larger than 10 MB.");
        return await SaveAsync(projectRoot, await File.ReadAllBytesAsync(sourcePath, ct), info.Extension, ct);
    }
}
