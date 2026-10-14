namespace Taverna.Core.Projects;

public sealed record FileEntry(string FullPath, bool IsDirectory, IReadOnlyList<FileEntry> Children);

public sealed class ProjectFiles
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".taverna", "bin", "obj", "node_modules"
    };

    public IReadOnlyList<FileEntry> List(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        return Scan(directory);
    }

    private static IReadOnlyList<FileEntry> Scan(string directory)
    {
        var entries = new List<FileEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(p => !Directory.Exists(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (Ignored.Contains(name)) continue;
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                entries.Add(new FileEntry(path, isDirectory, isDirectory ? Scan(path) : []));
            }
            catch (UnauthorizedAccessException) { /* Skip inaccessible entries. */ }
            catch (IOException) { /* Skip entries removed while refreshing. */ }
        }
        return entries;
    }
}
