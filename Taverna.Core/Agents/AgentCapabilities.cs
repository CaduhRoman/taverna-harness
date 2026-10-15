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

    /// <summary>Per-model labels and efforts when the CLI publishes them; models absent here accept every effort.</summary>
    public IReadOnlyList<AgentModel> Details { get; init; } = [];

    /// <summary>Model and effort values end up as process arguments, so only plain identifiers pass.</summary>
    public static bool IsSafeToken(string? value) => value is null || SafeToken.IsMatch(value);

    public static AgentCapabilities From(IReadOnlyList<AgentModel> models, IReadOnlyList<string> fallbackEfforts, bool supportsImages)
    {
        var efforts = models.SelectMany(m => m.Efforts).Distinct().OrderBy(EffortRank).ToList();
        return new AgentCapabilities(models.Select(m => m.Id).ToList(), efforts.Count > 0 ? efforts : fallbackEfforts, supportsImages)
        {
            Details = models
        };
    }

    public string Label(string model) => Details.FirstOrDefault(m => m.Id == model)?.Label ?? model;

    /// <summary>Efforts the model accepts, or null when unknown (any listed effort is offered).</summary>
    public IReadOnlyList<string>? EffortsFor(string? model) =>
        model is null ? null : Details.FirstOrDefault(m => m.Id == model) is { EffortsKnown: true } known ? known.Efforts : null;

    private static readonly string[] EffortOrder = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    private static int EffortRank(string effort) => Array.IndexOf(EffortOrder, effort) is var i and >= 0 ? i : int.MaxValue;
}

/// <summary>A model from the CLI's own catalog. EffortsKnown=false means the catalog did not say.</summary>
public sealed record AgentModel(string Id, string Label, IReadOnlyList<string> Efforts, bool EffortsKnown = true);

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
