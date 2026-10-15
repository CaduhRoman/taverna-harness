using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Taverna.Core.Agents;

/// <summary>
/// Reads the local Codex setup from CODEX_HOME (default ~/.codex): the configured model and base URL from
/// config.toml and the account's model list from models_cache.json, which Codex itself refreshes. Custom
/// providers such as the codex-chatgpt-web bridge show up there too.
/// </summary>
public sealed partial class CodexSetup(string home)
{
    private sealed record Snapshot(DateTime ConfigTime, DateTime CacheTime, string? ConfiguredModel, Uri? BaseUrl, IReadOnlyList<AgentModel> Models);

    private readonly object gate = new();
    private Snapshot? snapshot;

    public string Home { get; } = home;

    public static CodexSetup Default() => new(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    public string? ConfiguredModel => Current().ConfiguredModel;
    public Uri? BaseUrl => Current().BaseUrl;

    /// <summary>Models Codex lists for this account, the configured one first; hidden models are left out.</summary>
    public IReadOnlyList<AgentModel> Models => Current().Models;

    /// <summary>
    /// When Codex is routed to a loopback server (a local bridge), checks that something listens there.
    /// Without this, Codex retries and then waits for the network indefinitely.
    /// </summary>
    public async Task<string?> CheckLocalEndpointAsync(CancellationToken ct)
    {
        if (BaseUrl is not { IsLoopback: true } url) return null;
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(url.Host, url.Port, timeout.Token);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return $"Codex is routed to {url} (openai_base_url in {Path.Combine(Home, "config.toml")}), but nothing is listening there. " +
                   "If you use codex-chatgpt-web, open its launcher and check `codex-chatgpt-web doctor`.";
        }
    }

    private Snapshot Current()
    {
        var configPath = Path.Combine(Home, "config.toml");
        var cachePath = Path.Combine(Home, "models_cache.json");
        var configTime = File.Exists(configPath) ? File.GetLastWriteTimeUtc(configPath) : DateTime.MinValue;
        var cacheTime = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath) : DateTime.MinValue;
        lock (gate)
        {
            if (snapshot is { } s && s.ConfigTime == configTime && s.CacheTime == cacheTime) return s;
            var (model, baseUrl) = ReadConfig(configPath);
            var models = ReadModels(cachePath);
            if (model is not null)
            {
                var index = models.FindIndex(m => m.Id == model);
                var configured = index >= 0 ? models[index] : new AgentModel(model, model, [], EffortsKnown: false);
                if (index >= 0) models.RemoveAt(index);
                models.Insert(0, configured);
            }
            return snapshot = new Snapshot(configTime, cacheTime, model, baseUrl, models);
        }
    }

    /// <summary>Only the top-level keys Taverna needs; anything after the first [table] is ignored.</summary>
    private static (string? Model, Uri? BaseUrl) ReadConfig(string path)
    {
        string? model = null;
        Uri? baseUrl = null;
        try
        {
            if (!File.Exists(path)) return (null, null);
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) break;
                if (TopLevelString().Match(line) is not { Success: true } match) continue;
                var value = match.Groups["value"].Value;
                if (match.Groups["key"].Value == "model" && AgentCapabilities.IsSafeToken(value)) model = value;
                else if (match.Groups["key"].Value == "openai_base_url" && Uri.TryCreate(value, UriKind.Absolute, out var uri)) baseUrl = uri;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return (model, baseUrl);
    }

    private static List<AgentModel> ReadModels(string path)
    {
        var models = new List<AgentModel>();
        try
        {
            if (!File.Exists(path)) return models;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("models", out var list) || list.ValueKind != JsonValueKind.Array) return models;
            foreach (var item in list.EnumerateArray())
            {
                if (JsonEvents.String(item, "slug") is not { } slug || !AgentCapabilities.IsSafeToken(slug)) continue;
                if (JsonEvents.String(item, "visibility") is { } visibility && visibility != "list") continue;
                var efforts = new List<string>();
                if (item.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                    foreach (var level in levels.EnumerateArray())
                        if (JsonEvents.String(level, "effort") is { } effort && AgentCapabilities.IsSafeToken(effort))
                            efforts.Add(effort);
                models.Add(new AgentModel(slug, JsonEvents.String(item, "display_name") ?? slug, efforts, efforts.Count > 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return models;
    }

    [GeneratedRegex("""^(?<key>model|openai_base_url)\s*=\s*["'](?<value>[^"']*)["']""")]
    private static partial Regex TopLevelString();
}
