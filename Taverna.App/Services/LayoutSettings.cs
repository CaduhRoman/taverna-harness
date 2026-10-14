using System.Text.Json;

namespace Taverna.App.Services;

/// <summary>Window and panel proportions, remembered between sessions.</summary>
public sealed class LayoutSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Taverna", "layout.json");

    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 860;
    public bool Maximized { get; set; }
    public double ExplorerWidth { get; set; } = 260;
    public double ChatWidth { get; set; } = 340;
    public double SplitRatio { get; set; } = 0.5;
    public bool ExplorerVisible { get; set; } = true;
    public bool ChatVisible { get; set; } = true;
    public string? LastProjectDirectory { get; set; }

    public static LayoutSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new LayoutSettings();
            var settings = JsonSerializer.Deserialize<LayoutSettings>(File.ReadAllText(FilePath)) ?? new LayoutSettings();
            settings.Clamp();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new LayoutSettings();
        }
    }

    public void Save()
    {
        try
        {
            Clamp();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Layout is a convenience; failing to persist it must never block closing.
        }
    }

    private void Clamp()
    {
        WindowWidth = Math.Clamp(Finite(WindowWidth, 1440), 940, 8000);
        WindowHeight = Math.Clamp(Finite(WindowHeight, 860), 550, 8000);
        ExplorerWidth = Math.Clamp(Finite(ExplorerWidth, 260), 180, 640);
        ChatWidth = Math.Clamp(Finite(ChatWidth, 340), 260, 720);
        SplitRatio = Math.Clamp(Finite(SplitRatio, 0.5), 0.15, 0.85);
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
