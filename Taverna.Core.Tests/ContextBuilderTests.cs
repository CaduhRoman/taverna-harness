using Taverna.Core.Agents;
using Taverna.Core.Chat;
using Taverna.Core.Context;
using Xunit;

namespace Taverna.Core.Tests;

public sealed class ContextBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>n turns: user "u{i}" answered by agent "a{i}", one minute apart.</summary>
    private static List<ChatMessage> Conversation(int turns, int replyLength = 10)
    {
        var messages = new List<ChatMessage>();
        for (var i = 0; i < turns; i++)
        {
            messages.Add(new ChatMessage { Id = $"u{i}", Author = ChatAuthor.User, Timestamp = Start.AddMinutes(i * 2), Content = $"question {i}" });
            messages.Add(new ChatMessage
            {
                Id = $"a{i}", Author = i % 2 == 0 ? ChatAuthor.Claude : ChatAuthor.Codex, Timestamp = Start.AddMinutes(i * 2 + 1),
                ReplyToId = $"u{i}", Content = $"answer {i} " + new string('x', replyLength)
            });
        }
        return messages;
    }

    [Fact]
    public void SelectRecent_KeepsLastFifteenUserMessagesWithTheirReplies_InOrder()
    {
        var history = Conversation(20);
        var selected = ContextBuilder.SelectRecent(history.OrderBy(_ => Guid.NewGuid()), 15);

        var users = selected.Where(m => m.IsUser).Select(m => m.Id).ToList();
        Assert.Equal(Enumerable.Range(5, 15).Select(i => $"u{i}"), users);
        Assert.Equal(30, selected.Count);
        Assert.Equal(selected.OrderBy(m => m.Timestamp).Select(m => m.Id), selected.Select(m => m.Id));
        Assert.DoesNotContain(selected, m => m.Id == "a4");
    }

    [Fact]
    public void SelectRecent_AssociatesRepliesByReplyToAndByPosition()
    {
        var history = new List<ChatMessage>
        {
            new() { Id = "old", Author = ChatAuthor.User, Timestamp = Start, Content = "old" },
            new() { Id = "new", Author = ChatAuthor.User, Timestamp = Start.AddMinutes(1), Content = "new" },
            // A late reply to a message outside the window must not leak in.
            new() { Id = "lateOld", Author = ChatAuthor.Codex, Timestamp = Start.AddMinutes(2), ReplyToId = "old", Content = "late" },
            new() { Id = "claude", Author = ChatAuthor.Claude, Timestamp = Start.AddMinutes(3), ReplyToId = "new", Content = "c" },
            // No ReplyToId: belongs to the preceding user message.
            new() { Id = "copilot", Author = ChatAuthor.Copilot, Timestamp = Start.AddMinutes(4), Content = "p" },
            new() { Id = "empty", Author = ChatAuthor.Claude, Timestamp = Start.AddMinutes(5), ReplyToId = "new", Content = "  " }
        };

        var selected = ContextBuilder.SelectRecent(history, 1).Select(m => m.Id);

        Assert.Equal(["new", "claude", "copilot"], selected);
    }

    [Fact]
    public async Task Build_IncludesHistoryAndRequest_WhenItFits()
    {
        var builder = new ContextBuilder();
        var result = await builder.BuildAsync(Conversation(3), "what next?");

        Assert.False(result.Truncated);
        Assert.False(result.Summarized);
        Assert.Contains("[user · ", result.Prompt);
        Assert.Contains("[claude · ", result.Prompt);
        Assert.Contains("[codex · ", result.Prompt);
        Assert.True(result.Prompt.IndexOf("question 0", StringComparison.Ordinal) < result.Prompt.IndexOf("question 2", StringComparison.Ordinal));
        Assert.EndsWith("## Current request\nwhat next?\n", result.Prompt);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(1_000)]
    [InlineData(3_000)]
    [InlineData(24_000)]
    public async Task Build_NeverExceedsLimit_AndKeepsRequestAndRecentTurns(int limit)
    {
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = limit });
        var history = Conversation(15, replyLength: 2_000);

        var result = await builder.BuildAsync(history, "the current request");

        Assert.True(result.Prompt.Length <= limit, $"{result.Prompt.Length} > {limit}");
        Assert.EndsWith("the current request\n", result.Prompt);
        Assert.True(result.Truncated);
        Assert.DoesNotContain("question 0\n", result.Prompt);
        if (limit >= 3_000) Assert.Contains("question 14", result.Prompt);
    }

    [Fact]
    public async Task Build_TruncatesOversizedRequestToLimit()
    {
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 500 });
        var result = await builder.BuildAsync(Conversation(2), new string('r', 5_000));

        Assert.True(result.Prompt.Length <= 500);
        Assert.True(result.Truncated);
        Assert.Contains("rrrr", result.Prompt);
    }

    [Fact]
    public async Task Build_UsesSummaryForOverflow_AndStaysWithinLimit()
    {
        var summarizer = new FakeSummarizer("SUMMARY-OF-OLD-TURNS");
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 6_000 }, summarizer);

        var result = await builder.BuildAsync(Conversation(15, replyLength: 1_000), "go");

        Assert.True(result.Summarized);
        Assert.Contains("SUMMARY-OF-OLD-TURNS", result.Prompt);
        Assert.Contains("question 14", result.Prompt);
        Assert.True(result.Prompt.Length <= 6_000);
        Assert.Equal(1, summarizer.Calls);
        Assert.Contains("question 0", summarizer.Inputs[0]);
    }

    [Fact]
    public async Task Build_ReusesSummaryInsteadOfSummarizingAgain()
    {
        var summarizer = new FakeSummarizer("S1");
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 6_000 }, summarizer);
        var history = Conversation(15, replyLength: 1_000);

        var first = await builder.BuildAsync(history, "go");
        var second = await builder.BuildAsync(history, "go again", cache: first.Summary);

        Assert.Equal(1, summarizer.Calls);
        Assert.Same(first.Summary, second.Summary);
        Assert.Contains("S1", second.Prompt);
    }

    [Fact]
    public async Task Build_ExtendsPreviousSummaryWithOnlyNewOverflow()
    {
        var summarizer = new FakeSummarizer("S");
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 6_000 }, summarizer);
        var first = await builder.BuildAsync(Conversation(10, replyLength: 1_000), "go");

        // Two more turns push more history into the overflow while keeping the old prefix.
        await builder.BuildAsync(Conversation(12, replyLength: 1_000), "go", cache: first.Summary);

        Assert.Equal(2, summarizer.Calls);
        Assert.StartsWith("Previous summary:\nS", summarizer.Inputs[1]);
        Assert.DoesNotContain("question 0\n", summarizer.Inputs[1]);
    }

    [Fact]
    public async Task Build_FallsBackToTruncation_WhenCopilotIsUnavailable()
    {
        var copilot = new ScriptedRunner(AgentKind.Copilot, (_, _) => ScriptedRunner.Reply("never"), available: false);
        var summarizer = new AgentSummarizer(copilot);
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 3_000 }, summarizer);

        var result = await builder.BuildAsync(Conversation(15, replyLength: 1_000), "go");

        Assert.False(summarizer.IsAvailable);
        Assert.False(result.Summarized);
        Assert.True(result.Truncated);
        Assert.Empty(copilot.Requests);
        Assert.True(result.Prompt.Length <= 3_000);
    }

    [Fact]
    public async Task Build_FallsBackToTruncation_WhenSummarizerFails()
    {
        var copilot = new ScriptedRunner(AgentKind.Copilot, (_, _) => ScriptedRunner.Reply("boom", exitCode: 1));
        var builder = new ContextBuilder(new ContextOptions { MaxCharacters = 3_000 }, new AgentSummarizer(copilot));

        var result = await builder.BuildAsync(Conversation(15, replyLength: 1_000), "go");

        Assert.Single(copilot.Requests);
        Assert.Equal(AgentRunMode.Summary, copilot.Requests[0].Mode);
        Assert.False(result.Summarized);
        Assert.True(result.Truncated);
        Assert.DoesNotContain("boom", result.Prompt);
        Assert.True(result.Prompt.Length <= 3_000);
    }

    private sealed class FakeSummarizer(string summary) : IContextSummarizer
    {
        public int Calls { get; private set; }
        public List<string> Inputs { get; } = [];
        public bool IsAvailable => true;

        public Task<string?> SummarizeAsync(string conversation, int maxCharacters, CancellationToken ct)
        {
            Calls++;
            Inputs.Add(conversation);
            return Task.FromResult<string?>(summary);
        }
    }
}
