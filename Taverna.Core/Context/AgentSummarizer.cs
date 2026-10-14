using System.Text;
using Taverna.Core.Agents;

namespace Taverna.Core.Context;

/// <summary>
/// Summarizes old chat turns with an agent CLI in Summary mode (no file edits). Used with Copilot;
/// unavailable whenever that CLI is missing, in which case the context builder truncates instead.
/// </summary>
public sealed class AgentSummarizer(IAgentRunner runner, TimeSpan? timeout = null) : IContextSummarizer
{
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(90);

    public bool IsAvailable => runner.IsAvailable();

    public async Task<string?> SummarizeAsync(string conversation, int maxCharacters, CancellationToken ct)
    {
        var prompt =
            $"Summarize the conversation below between a user and coding agents in at most {maxCharacters} characters. " +
            "Keep decisions, file names, code identifiers, constraints and open questions. Reply with the summary only.\n\n" +
            conversation;

        // An empty scratch directory: the summarizer must not see or touch project files.
        var scratch = Directory.CreateTempSubdirectory("taverna-summary-").FullName;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var text = new StringBuilder();
        int? exitCode = null;
        try
        {
            await foreach (var e in runner.RunAsync(new AgentRequest(prompt, scratch) { Mode = AgentRunMode.Summary }, limit.Token))
            {
                if (e.Kind == AgentEventKind.Text) text.Append(e.Text);
                else if (e.Kind == AgentEventKind.Exit) exitCode = e.ExitCode;
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }
        ct.ThrowIfCancellationRequested();
        return exitCode == 0 && text.Length > 0 ? text.ToString().Trim() : null;
    }
}
