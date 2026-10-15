using System.Text.Json;
using Taverna.Core.IO;

namespace Taverna.Core.Agents;

public enum AgentPermissionMode { Manual, Auto, Plan }

public sealed record PermissionOption(AgentPermissionMode Mode, bool IsSupported, string Description);

/// <summary>Capabilities of the current non-interactive transports, not promises made in a prompt.</summary>
public static class AgentPermissions
{
    public static IReadOnlyList<PermissionOption> For(AgentKind agent) =>
    [
        new(AgentPermissionMode.Manual, false, "Approve each change. Approval channel unavailable."),
        new(AgentPermissionMode.Auto, agent != AgentKind.ExternalApi, agent switch
        {
            AgentKind.ExternalApi => "This API has no development tools.",
            AgentKind.Codex => "Commands and edits with the CLI workspace-write policy. No approval escalation.",
            _ => "Commands and edits without individual approval. No OS sandbox."
        }),
        new(AgentPermissionMode.Plan, agent == AgentKind.ExternalApi, agent == AgentKind.ExternalApi
            ? "Analysis only. No local tools or file access."
            : "Unavailable: read-only execution is not guaranteed for all configured tools.")
    ];

    public static bool Supports(AgentKind agent, AgentPermissionMode mode) =>
        For(agent).Any(option => option.Mode == mode && option.IsSupported);

    public static string? Validate(AgentKind agent, AgentPermissionMode? mode) =>
        mode is { } value && !Supports(agent, value)
            ? $"{AgentNames.Display(agent)} does not support {value} permissions in this integration." : null;
}

/// <summary>User preference only; an unavailable/corrupt value never grants Auto.</summary>
public sealed class PermissionPreferences(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Taverna", "permissions.json");

    public Dictionary<AgentKind, AgentPermissionMode> Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<AgentKind, AgentPermissionMode>>(
                File.ReadAllText(path), TavernaJson.Options) ?? [];
            return saved.Where(p => Enum.IsDefined(p.Key) && AgentPermissions.Supports(p.Key, p.Value))
                .ToDictionary(p => p.Key, p => p.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public Task SaveAsync(IReadOnlyDictionary<AgentKind, AgentPermissionMode> choices) =>
        AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(choices, TavernaJson.Options));
}
