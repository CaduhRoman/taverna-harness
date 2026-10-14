using System.Text;

namespace Taverna.Core.Projects;

public sealed class GitIgnoreService
{
    public async Task EnsureTavernaIgnoredAsync(string projectRoot, CancellationToken ct = default)
    {
        // Do not write to folders that are not themselves Git repository roots.
        if (!Directory.Exists(Path.Combine(projectRoot, ".git")) &&
            !File.Exists(Path.Combine(projectRoot, ".git"))) return;

        var path = Path.Combine(projectRoot, ".gitignore");
        var contents = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        if (contents.Split('\n').Any(line => line.Trim().TrimEnd('\r') is ".taverna" or ".taverna/")) return;

        var prefix = contents.Length > 0 && !contents.EndsWith('\n') ? Environment.NewLine : "";
        await File.AppendAllTextAsync(path, prefix + ".taverna/" + Environment.NewLine, Encoding.UTF8, ct);
    }
}
