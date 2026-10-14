using System.Text.Json;
using Taverna.Core.Context;
using Taverna.Core.IO;

namespace Taverna.Core.Chat;

public sealed class ChatHistory
{
    public int Version { get; set; } = 1;
    public List<ChatMessage> Messages { get; set; } = [];
    /// <summary>Summary of older turns already produced, reused instead of re-summarizing.</summary>
    public SummaryCache? Summary { get; set; }
}

/// <summary>Reads and atomically writes .taverna/chat.json.</summary>
public sealed class ChatStore(string projectRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public string FilePath { get; } = Path.Combine(projectRoot, ".taverna", "chat.json");

    /// <summary>Replies still streaming when the app stopped keep their partial text and become Failed/Interrupted.</summary>
    public async Task<ChatHistory> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(FilePath)) return new ChatHistory();
        ChatHistory history;
        try
        {
            history = JsonSerializer.Deserialize<ChatHistory>(await File.ReadAllTextAsync(FilePath, ct), TavernaJson.Options) ?? new();
        }
        catch (JsonException)
        {
            TavernaJson.QuarantineCorrupt(FilePath);
            return new ChatHistory();
        }
        foreach (var message in history.Messages.Where(m => m.State == ChatMessageState.Streaming))
        {
            message.State = ChatMessageState.Failed;
            message.Error ??= "Interrupted";
        }
        return history;
    }

    public async Task SaveAsync(ChatHistory history, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { await AtomicFile.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(history, TavernaJson.Options), ct); }
        finally { gate.Release(); }
    }
}
