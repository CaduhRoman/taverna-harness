using System.Text.Json;

namespace Taverna.Core.Agents;

internal static class JsonEvents
{
    public static string? String(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public static bool Property(JsonElement obj, string property, out JsonElement result)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out result)) return true;
        result = default;
        return false;
    }

    /// <summary>Reads a top-level "usage" object.</summary>
    public static TokenUsage? Tokens(JsonElement root) => Property(root, "usage", out var usage) ? Usage(usage) : null;

    /// <summary>Reads Anthropic/OpenAI style usage counters from the usage object itself.</summary>
    public static TokenUsage? Usage(JsonElement usage)
    {
        var input = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens");
        var output = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens");
        if (input is null || output is null) return null;
        return new TokenUsage(input.Value, output.Value,
            Number(usage, "cached_input_tokens") ?? Number(usage, "cache_read_input_tokens"));
    }

    private static long? Number(JsonElement obj, string key) =>
        Property(obj, key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
