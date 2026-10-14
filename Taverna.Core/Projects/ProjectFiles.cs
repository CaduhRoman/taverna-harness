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

    public string CreateFile(string projectRoot, string parentDirectory, string name)
    {
        var path = ResolveNewEntry(projectRoot, parentDirectory, name);
        // CreateNew never overwrites a file created concurrently after the existence check.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        return path;
    }

    public string CreateDirectory(string projectRoot, string parentDirectory, string name)
    {
        var path = ResolveNewEntry(projectRoot, parentDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveNewEntry(string projectRoot, string parentDirectory, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Informe um nome.");
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException($"Nome inválido: {name}");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory));
        if (!parent.Equals(root, comparison) && !parent.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("A pasta de destino precisa ficar dentro do projeto.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var path = Path.Combine(parent, name);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"Já existe um item chamado {name}.");
        return path;
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
