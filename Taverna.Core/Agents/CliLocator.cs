namespace Taverna.Core.Agents;

public static class CliLocator
{
    public static string? Find(string command)
    {
        if (Path.IsPathRooted(command)) return File.Exists(command) ? command : null;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var part in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = part.Trim().Trim('"');
            foreach (var extension in extensions.Prepend(""))
            {
                var candidate = Path.Combine(dir, command + extension.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
                // On Linux filenames are case sensitive; on Windows PATHEXT is conventionally uppercase.
                if (OperatingSystem.IsWindows())
                {
                    candidate = Path.Combine(dir, command + extension.ToUpperInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        return null;
    }
}
