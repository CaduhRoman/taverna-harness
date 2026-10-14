using Taverna.Core.Agents;

namespace Taverna.App.ViewModels;

/// <summary>An agent as offered in pickers, with its availability and the models/efforts it accepts.</summary>
public sealed class AgentOptionViewModel(IAgentRunner runner)
{
    public const string DefaultChoice = "Default";

    public IAgentRunner Runner { get; } = runner;
    public AgentKind Kind => Runner.Kind;
    public string Name => AgentNames.Display(Runner.Kind);
    public bool IsAvailable { get; private set; } = runner.IsAvailable();
    public bool SupportsImages => Runner.Capabilities.SupportsImages;
    public bool SupportsEffort => Runner.Capabilities.Efforts.Count > 0;
    public string AvailabilityText => IsAvailable ? "Ready" : Kind == AgentKind.ExternalApi ? "Not configured" : "Not installed";

    /// <summary>"Default" maps to the CLI's own configured model.</summary>
    public IReadOnlyList<string> Models => [DefaultChoice, .. Runner.Capabilities.Models];
    public IReadOnlyList<string> Efforts => [DefaultChoice, .. Runner.Capabilities.Efforts];

    public void RefreshAvailability() => IsAvailable = Runner.IsAvailable();

    public static string? Normalize(string? choice) =>
        string.IsNullOrWhiteSpace(choice) || choice == DefaultChoice ? null : choice.Trim();
}
