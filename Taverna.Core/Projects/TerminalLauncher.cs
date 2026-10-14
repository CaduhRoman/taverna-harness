using System.Diagnostics;

namespace Taverna.Core.Projects;

public sealed class TerminalLauncher
{
    public void Open(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        if (OperatingSystem.IsWindows())
        {
            _ = Process.Start(new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = directory,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Falha ao iniciar cmd.exe.");
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
            {
                if (!IsOnPath(name)) continue;
                try
                {
                    Process.Start(new ProcessStartInfo(name)
                    {
                        WorkingDirectory = directory,
                        UseShellExecute = true
                    });
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { /* Try next candidate. */ }
            }
            throw new InvalidOperationException("Nenhum terminal gráfico compatível foi encontrado.");
        }

        throw new PlatformNotSupportedException("Terminal externo compatível apenas com Windows e Linux.");
    }

    private static bool IsOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => File.Exists(Path.Combine(folder, executable)));
}
