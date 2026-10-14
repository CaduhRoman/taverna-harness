using System.Text;
using Taverna.Core.Chat;

namespace Taverna.Core.Context;

public sealed record ContextOptions
{
    public int MaxCharacters { get; init; } = 24_000;
    public int MaxUserMessages { get; init; } = 15;
    /// <summary>Upper bound for the summary of older turns; at most a quarter of the history budget is used.</summary>
    public int MaxSummaryCharacters { get; init; } = 4_000;
    /// <summary>Text sent to the summarizer is capped too, keeping its newest part.</summary>
    public int MaxSummarizerInput { get; init; } = 20_000;
}

/// <summary>Summary text plus the ids of the messages it covers, in order.</summary>
public sealed class SummaryCache
{
    public List<string> CoveredIds { get; set; } = [];
    public string Summary { get; set; } = "";
}

public sealed record ContextResult(
    string Prompt,
    IReadOnlyList<string> IncludedMessageIds,
    bool Summarized,
    bool Truncated,
    SummaryCache? Summary);

public interface IContextSummarizer
{
    bool IsAvailable { get; }
    /// <summary>Returns null on failure; callers then fall back to truncation.</summary>
    Task<string?> SummarizeAsync(string conversation, int maxCharacters, CancellationToken ct);
}

/// <summary>
/// Builds the prompt sent to an agent: the last N user messages with their replies, oldest first,
/// then the new request, never exceeding <see cref="ContextOptions.MaxCharacters"/>. When history does
/// not fit, recent turns win; older ones are summarized (if a summarizer works) or dropped.
/// </summary>
public sealed class ContextBuilder(ContextOptions? options = null, IContextSummarizer? summarizer = null)
{
    private const string Header =
        "You are a coding agent working inside Taverna. Claude, Codex and Copilot share this single conversation, " +
        "so earlier replies may come from any of them. Your working directory is the project root.\n\n";
    private const string HistoryHeading = "## Conversation\n";
    private const string SummaryHeading = "## Earlier conversation (summary)\n";
    private const string RequestHeading = "## Current request\n";
    private const string TruncatedMarker = "[…earlier content truncated]\n";
    private const string RequestCutMarker = "\n[…request truncated]";

    public ContextOptions Options { get; } = options ?? new ContextOptions();

    private sealed record Turn(List<ChatMessage> Messages, string Text);

    /// <summary>The last <paramref name="maxUserMessages"/> user messages and the agent replies that answer them, chronologically.</summary>
    public static List<ChatMessage> SelectRecent(IEnumerable<ChatMessage> history, int maxUserMessages)
    {
        var ordered = history.OrderBy(m => m.Timestamp).ToList();
        var selectedUsers = ordered.Where(m => m.IsUser).TakeLast(maxUserMessages).Select(m => m.Id).ToHashSet();
        var result = new List<ChatMessage>();
        string? currentUser = null;
        foreach (var message in ordered)
        {
            if (message.IsUser)
            {
                currentUser = message.Id;
                if (selectedUsers.Contains(message.Id)) result.Add(message);
            }
            else if ((message.ReplyToId ?? currentUser) is { } owner && selectedUsers.Contains(owner) &&
                     !string.IsNullOrWhiteSpace(message.Content))
                result.Add(message);
        }
        return result;
    }

    public async Task<ContextResult> BuildAsync(IReadOnlyList<ChatMessage> history, string request,
        IReadOnlyList<string>? requestAttachments = null, SummaryCache? cache = null, CancellationToken ct = default)
    {
        var max = Options.MaxCharacters;
        var requestSection = RequestHeading + request.Trim() + AttachmentNote(requestAttachments) + "\n";
        var header = Header;

        // The current request has absolute priority; even the header yields to it.
        if (header.Length + requestSection.Length > max) header = "";
        if (requestSection.Length > max)
        {
            var keep = Math.Max(0, max - RequestCutMarker.Length - 1);
            requestSection = requestSection[..keep] + RequestCutMarker + "\n";
            if (requestSection.Length > max) requestSection = requestSection[..max];
            return new ContextResult(requestSection, [], false, true, cache);
        }

        var available = max - header.Length - requestSection.Length;
        var turns = BuildTurns(SelectRecent(history, Options.MaxUserMessages));
        var allText = turns.Sum(t => t.Text.Length);

        if (turns.Count == 0 || HistoryHeading.Length + allText <= available)
            return Assemble(header, null, turns, null, requestSection, summarized: false, truncated: false, cache);

        // History overflows. With a summarizer, reserve room for a summary of what does not fit.
        if (summarizer is { IsAvailable: true })
        {
            var reserve = Math.Min(available / 4, Options.MaxSummaryCharacters);
            var (recent, older) = Fit(turns, available - reserve - HistoryHeading.Length);
            var overflow = older.SelectMany(t => t.Messages).ToList();
            var summaryBudget = reserve - SummaryHeading.Length - 2;
            if (summaryBudget > 0 && await SummarizeAsync(overflow, cache, summaryBudget, ct) is { } summary)
            {
                var text = summary.Summary.Length > summaryBudget ? summary.Summary[..summaryBudget] : summary.Summary;
                return Assemble(header, text, recent, null, requestSection, summarized: true, truncated: false, summary);
            }
        }

        // Fallback: keep whole recent turns, then as much of the next older turn as still fits.
        var (kept, dropped) = Fit(turns, available - HistoryHeading.Length);
        string? partial = null;
        // Minus one for the newline appended after the partial block.
        var room = available - HistoryHeading.Length - kept.Sum(t => t.Text.Length) - TruncatedMarker.Length - 1;
        if (dropped.Count > 0 && room > 200)
        {
            var last = dropped[^1].Text;
            partial = TruncatedMarker + last[^Math.Min(room, last.Length)..];
        }
        return Assemble(header, null, kept, partial, requestSection, summarized: false, truncated: true, cache);
    }

    private async Task<SummaryCache?> SummarizeAsync(List<ChatMessage> overflow, SummaryCache? cache, int maxChars, CancellationToken ct)
    {
        var ids = overflow.Select(m => m.Id).ToList();
        if (ids.Count == 0) return null;
        // Same messages as last time: reuse without calling the summarizer again.
        if (cache is not null && cache.CoveredIds.SequenceEqual(ids)) return cache;

        string input;
        var isExtension = cache is { CoveredIds.Count: > 0 } && ids.Count > cache.CoveredIds.Count &&
                          ids.Take(cache.CoveredIds.Count).SequenceEqual(cache.CoveredIds);
        if (isExtension)
            // Only the newly overflowed messages are new; the old part is already condensed.
            input = "Previous summary:\n" + cache!.Summary + "\n\nNewer messages:\n" +
                    string.Concat(overflow.Skip(cache.CoveredIds.Count).Select(Format));
        else
            input = string.Concat(overflow.Select(Format));
        if (input.Length > Options.MaxSummarizerInput) input = input[^Options.MaxSummarizerInput..];

        string? summary;
        try { summary = await summarizer!.SummarizeAsync(input, maxChars, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { summary = null; }
        return string.IsNullOrWhiteSpace(summary) ? null : new SummaryCache { CoveredIds = ids, Summary = summary.Trim() };
    }

    /// <summary>Newest turns that fit in <paramref name="budget"/>, plus the older remainder.</summary>
    private static (List<Turn> Recent, List<Turn> Older) Fit(List<Turn> turns, int budget)
    {
        var used = 0;
        var start = turns.Count;
        while (start > 0 && used + turns[start - 1].Text.Length <= budget)
        {
            used += turns[start - 1].Text.Length;
            start--;
        }
        return (turns.GetRange(start, turns.Count - start), turns.GetRange(0, start));
    }

    private ContextResult Assemble(string header, string? summary, List<Turn> turns, string? partial, string requestSection,
        bool summarized, bool truncated, SummaryCache? cache)
    {
        var builder = new StringBuilder(header);
        if (summary is not null) builder.Append(SummaryHeading).Append(summary).Append("\n\n");
        if (turns.Count > 0 || partial is not null)
        {
            builder.Append(HistoryHeading);
            if (partial is not null) builder.Append(partial).Append('\n');
            foreach (var turn in turns) builder.Append(turn.Text);
        }
        builder.Append(requestSection);
        var prompt = builder.ToString();
        // Defensive: the arithmetic above already guarantees this; the request sits at the end and survives.
        if (prompt.Length > Options.MaxCharacters) prompt = prompt[^Options.MaxCharacters..];
        return new ContextResult(prompt, turns.SelectMany(t => t.Messages).Select(m => m.Id).ToList(), summarized, truncated, cache);
    }

    private static List<Turn> BuildTurns(List<ChatMessage> messages)
    {
        var turns = new List<List<ChatMessage>>();
        foreach (var message in messages)
        {
            if (message.IsUser || turns.Count == 0) turns.Add([message]);
            else turns[^1].Add(message);
        }
        return turns.Select(t => new Turn(t, string.Concat(t.Select(Format)))).ToList();
    }

    public static string Format(ChatMessage message)
    {
        var reference = message.Branch is null ? "" : $" · {message.Branch}@{message.Commit}";
        var incomplete = message.State is ChatMessageState.Failed or ChatMessageState.Cancelled ? " (incomplete)" : "";
        return $"[{ChatMessage.AuthorName(message.Author)} · {message.Timestamp:yyyy-MM-dd HH:mm}{reference}]{incomplete}\n" +
               message.Content.Trim() + AttachmentNote(message.Attachments) + "\n\n";
    }

    private static string AttachmentNote(IReadOnlyList<string>? attachments) =>
        attachments is { Count: > 0 } ? $"\n[attachments: {string.Join(", ", attachments.Select(Path.GetFileName))}]" : "";
}
