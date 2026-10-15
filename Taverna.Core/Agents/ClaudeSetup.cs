using System.Text.Json;

namespace Taverna.Core.Agents;

/// <summary>
/// Reads the model catalog Claude Code caches for the signed-in account (cache/model-catalog under
/// CLAUDE_CONFIG_DIR, default ~/.claude), the same list its own model picker shows, plus extra options
/// such as 1M-context variants from .claude.json.
/// </summary>
public sealed class ClaudeSetup(string configDirectory, string globalConfigPath)
{
    private sealed record Snapshot(string Stamp, IReadOnlyList<AgentModel> Models);

    private readonly object gate = new();
    private Snapshot? snapshot;

    public static ClaudeSetup Default()
    {
        if (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom)
            return new ClaudeSetup(custom, Path.Combine(custom, ".claude.json"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new ClaudeSetup(Path.Combine(home, ".claude"), Path.Combine(home, ".claude.json"));
    }

    /// <summary>Main models first, then older ones, in catalog order. Empty when no catalog is cached.</summary>
    public IReadOnlyList<AgentModel> Models
    {
        get
        {
            var catalogs = CatalogFiles();
            var stamp = string.Join("|", catalogs.Select(f => $"{f}:{File.GetLastWriteTimeUtc(f).Ticks}")) +
                        $"|{(File.Exists(globalConfigPath) ? File.GetLastWriteTimeUtc(globalConfigPath).Ticks : 0)}";
            lock (gate)
            {
                if (snapshot is { } s && s.Stamp == stamp) return s.Models;
                var models = ReadCatalog(catalogs);
                if (models.Count > 0) AddExtraOptions(models);
                return (snapshot = new Snapshot(stamp, models)).Models;
            }
        }
    }

    /// <summary>Catalogs for Claude Code ("cc") first, newest first; other surfaces only as a fallback.</summary>
    private List<string> CatalogFiles()
    {
        var directory = Path.Combine(configDirectory, "cache", "model-catalog");
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.GetFiles(directory, "*.json")
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f).EndsWith("-cc", StringComparison.Ordinal))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static List<AgentModel> ReadCatalog(List<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!JsonEvents.Property(doc.RootElement, "catalog", out var catalog) ||
                    !JsonEvents.Property(catalog, "config", out var config) ||
                    !JsonEvents.Property(config, "models", out var list) || list.ValueKind != JsonValueKind.Array) continue;

                var main = new List<AgentModel>();
                var rest = new List<AgentModel>();
                foreach (var item in list.EnumerateArray())
                {
                    if (JsonEvents.String(item, "id") is not { } id || !AgentCapabilities.IsSafeToken(id)) continue;
                    var efforts = new List<string>();
                    if (JsonEvents.Property(item, "thinking", out var thinking) &&
                        JsonEvents.Property(thinking, "effort_options", out var options) && options.ValueKind == JsonValueKind.Array)
                        foreach (var option in options.EnumerateArray())
                            if (JsonEvents.String(option, "id") is { } effort && AgentCapabilities.IsSafeToken(effort))
                                efforts.Add(effort);
                    var model = new AgentModel(id, JsonEvents.String(item, "name") ?? id, efforts);
                    (JsonEvents.String(item, "section") == "main" ? main : rest).Add(model);
                }
                if (main.Count + rest.Count > 0) return [.. main, .. rest];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return [];
    }

    /// <summary>Variants such as "claude-fable-5-1[1m]" that Claude Code offers in addition to the catalog.</summary>
    private void AddExtraOptions(List<AgentModel> models)
    {
        try
        {
            if (!File.Exists(globalConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(globalConfigPath));
            if (!JsonEvents.Property(doc.RootElement, "additionalModelOptionsCache", out var extras) ||
                extras.ValueKind != JsonValueKind.Array) return;
            foreach (var extra in extras.EnumerateArray())
            {
                if (JsonEvents.String(extra, "value") is not { } id || !AgentCapabilities.IsSafeToken(id) ||
                    models.Any(m => m.Id == id)) continue;
                var baseId = id.EndsWith("[1m]", StringComparison.Ordinal) ? id[..^4] : id;
                var index = models.FindIndex(m => m.Id == baseId);
                if (index < 0)
                {
                    models.Add(new AgentModel(id, JsonEvents.String(extra, "label") ?? id, [], EffortsKnown: false));
                    continue;
                }
                var label = baseId == id ? models[index].Label : $"{models[index].Label} (1M context)";
                models.Insert(index + 1, models[index] with { Id = id, Label = label });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
