using Taverna.Core.Agents;

namespace Taverna.Core.Chat;

public enum ChatAuthor { User, Claude, Codex, Copilot, ExternalApi }

public enum ChatMessageState { Complete, Streaming, Failed, Cancelled }

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public ChatAuthor Author { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    /// <summary>Branch and short commit of the project when the message was sent; null outside Git.</summary>
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    public string Content { get; set; } = "";
    /// <summary>Paths relative to the project root, under .taverna/attachments/.</summary>
    public List<string> Attachments { get; set; } = [];
    /// <summary>For agent replies: the user message being answered.</summary>
    public string? ReplyToId { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public ChatMessageState State { get; set; } = ChatMessageState.Complete;
    public string? Error { get; set; }
    public TokenUsage? Tokens { get; set; }

    public bool IsUser => Author == ChatAuthor.User;

    public ChatMessage Clone()
    {
        var copy = (ChatMessage)MemberwiseClone();
        copy.Attachments = [.. Attachments];
        return copy;
    }

    public static ChatAuthor AuthorFor(AgentKind kind) => kind switch
    {
        AgentKind.Claude => ChatAuthor.Claude,
        AgentKind.Codex => ChatAuthor.Codex,
        AgentKind.Copilot => ChatAuthor.Copilot,
        _ => ChatAuthor.ExternalApi
    };

    public static string AuthorName(ChatAuthor author) => author switch
    {
        ChatAuthor.User => "user",
        ChatAuthor.ExternalApi => "api",
        _ => author.ToString().ToLowerInvariant()
    };
}
