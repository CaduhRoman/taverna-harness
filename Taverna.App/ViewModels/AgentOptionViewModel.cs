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

    /// <summary>Efforts the chosen model accepts; models without catalog data get the full list.</summary>
    public IReadOnlyList<string> EffortsFor(string? model) =>
        Runner.Capabilities.EffortsFor(Normalize(model)) is { } known ? [DefaultChoice, .. known] : Efforts;

    /// <summary>Display name from the CLI catalog ("Opus 5.5"), or the id itself.</summary>
    public string Label(string model) => Runner.Capabilities.Label(model);

    public IReadOnlyList<ChoiceViewModel> ModelChoices => Models.Select(model => new ChoiceViewModel(model, Label(model))).ToList();
    public string ValidEffort(string? model, string? effort) => EffortsFor(model).Contains(effort)
        ? effort! : DefaultChoice;

    public void RefreshAvailability() => IsAvailable = Runner.IsAvailable();

    public static string? Normalize(string? choice) =>
        string.IsNullOrWhiteSpace(choice) || choice == DefaultChoice ? null : choice.Trim();
}
