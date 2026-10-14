using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Taverna.Core.Agents;

/// <summary>
/// Any OpenAI-compatible Chat Completions endpoint, configured through environment variables:
/// TAVERNA_API_BASE_URL (e.g. https://api.openai.com/v1), TAVERNA_API_KEY and TAVERNA_API_MODELS
/// (comma separated; the first is the default).
/// </summary>
public sealed record ExternalApiOptions(string? BaseUrl, string? ApiKey, IReadOnlyList<string> Models)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey) && Models.Count > 0;

    public static ExternalApiOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("TAVERNA_API_BASE_URL"),
        Environment.GetEnvironmentVariable("TAVERNA_API_KEY"),
        (Environment.GetEnvironmentVariable("TAVERNA_API_MODELS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>Chat only: the endpoint has no access to the working directory.</summary>
public sealed class ExternalApiRunner(ExternalApiOptions options, HttpClient? http = null) : IAgentRunner
{
    private readonly HttpClient http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public AgentKind Kind => AgentKind.ExternalApi;
    public string Name => "External API";
    public AgentCapabilities Capabilities => new(options.Models, ["low", "medium", "high"], SupportsImages: false);
    public bool IsAvailable() => options.IsConfigured;

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!options.IsConfigured)
        {
            yield return new AgentEvent(AgentEventKind.Error, "External API is not configured (TAVERNA_API_BASE_URL, TAVERNA_API_KEY, TAVERNA_API_MODELS).");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }
        if (request.Images.Count > 0)
        {
            yield return new AgentEvent(AgentEventKind.Error, "External API does not accept image attachments.");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }

        var body = new JsonObject
        {
            ["model"] = request.Model ?? options.Models[0],
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = request.Prompt } }
        };
        if (request.Effort is { } effort) body["reasoning_effort"] = effort;

        using var message = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl!.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        HttpResponseMessage? response = null;
        string? failure = null;
        var cancelled = false;
        try { response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
        catch (HttpRequestException ex) { failure = ex.Message; }

        if (cancelled) { yield return new AgentEvent(AgentEventKind.Cancelled); yield break; }
        if (response is null)
        {
            yield return new AgentEvent(AgentEventKind.Error, $"External API request failed: {failure}");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(CancellationToken.None);
                yield return new AgentEvent(AgentEventKind.Error, $"External API returned {(int)response.StatusCode}: {Trim(detail)}");
                yield return new AgentEvent(AgentEventKind.Exit, ExitCode: (int)response.StatusCode);
                yield break;
            }

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(CancellationToken.None));
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(ct); }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (IOException ex) { failure = ex.Message; break; }
                if (line is null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                foreach (var item in ParseChunk(data)) yield return item;
            }
        }

        if (cancelled) { yield return new AgentEvent(AgentEventKind.Cancelled); yield break; }
        if (failure is not null)
        {
            yield return new AgentEvent(AgentEventKind.Error, $"External API stream failed: {failure}");
            yield return new AgentEvent(AgentEventKind.Exit, ExitCode: -1);
            yield break;
        }
        yield return new AgentEvent(AgentEventKind.Exit, ExitCode: 0);
    }

    private static IEnumerable<AgentEvent> ParseChunk(string data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { return [new AgentEvent(AgentEventKind.Log, $"Unparsed chunk: {Trim(data)}")]; }
        using (doc)
        {
            var events = new List<AgentEvent>();
            var root = doc.RootElement;
            if (JsonEvents.Property(root, "choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (JsonEvents.Property(choice, "delta", out var delta) && JsonEvents.String(delta, "content") is { Length: > 0 } text)
                        events.Add(new AgentEvent(AgentEventKind.Text, text));
            if (JsonEvents.Property(root, "usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                JsonEvents.Usage(usage) is { } tokens)
                events.Add(new AgentEvent(AgentEventKind.Tokens, Usage: tokens));
            return events;
        }
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
