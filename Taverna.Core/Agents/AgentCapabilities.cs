using System.Text.RegularExpressions;

namespace Taverna.Core.Agents;

/// <summary>
/// What a runner accepts. Models and efforts are suggestions shown in the picker; null means "CLI default".
/// SupportsImages is only true where the image input mechanism was confirmed against the CLI.
/// </summary>
public sealed record AgentCapabilities(
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Efforts,
    bool SupportsImages,
    bool AllowsCustomModel = true)
{
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]\-]{0,99}$", RegexOptions.Compiled);

    /// <summary>Model and effort values end up as process arguments, so only plain identifiers pass.</summary>
    public static bool IsSafeToken(string? value) => value is null || SafeToken.IsMatch(value);
}

public static class AgentNames
{
    public static string Display(AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        AgentKind.Copilot => "Copilot",
        AgentKind.ExternalApi => "API",
        _ => kind.ToString()
    };

    /// <summary>Lowercase identifier used in branch names and logs.</summary>
    public static string Slug(AgentKind kind) => kind switch
    {
        AgentKind.ExternalApi => "api",
        _ => kind.ToString().ToLowerInvariant()
    };
}
