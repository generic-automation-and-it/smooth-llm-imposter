using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Reads the recognised upstream usage counters (HLD 016 spec 02 section 10.1, spec 03 section 7.1).
/// A count is a finite JSON number &gt;= 0; fractional values are floored. Strings, booleans, nulls,
/// negatives and non-finite values are treated as absent.
/// </summary>
internal static class AnthropicChatUsageReader
{
    private static readonly string[] CacheReadPaths =
    [
        "prompt_tokens_details.cached_tokens",
        "input_tokens_details.cached_tokens",
        "cached_tokens",
        "prompt_cache_hit_tokens",
        "cache_read_input_tokens",
        "cache_read_tokens"
    ];

    private static readonly string[] CacheWritePaths =
    [
        "prompt_tokens_details.cache_write_tokens",
        "prompt_tokens_details.cache_creation_tokens",
        "input_tokens_details.cache_write_tokens",
        "input_tokens_details.cache_creation_tokens",
        "cache_creation_input_tokens",
        "cache_creation_tokens",
        "cache_write_tokens"
    ];

    internal sealed record Counts(
        double? Prompt,
        double? Completion,
        double? Total,
        double? CacheRead,
        double? CacheWrite)
    {
        public bool HasRecognisedCounter =>
            Prompt is not null || Completion is not null || Total is not null ||
            CacheRead is not null || CacheWrite is not null;

        // Placeholders are objects with no recognized counter, or with every recognized counter zero.
        public bool IsPlaceholder =>
            (Prompt is null or 0) && (Completion is null or 0) && (Total is null or 0) &&
            (CacheRead is null or 0) && (CacheWrite is null or 0);
    }

    public static Counts Read(JsonObject usage)
    {
        double? prompt = First(usage, "prompt_tokens") ?? First(usage, "input_tokens");
        if (prompt is null &&
            First(usage, "prompt_cache_hit_tokens") is double hit &&
            First(usage, "prompt_cache_miss_tokens") is double miss)
        {
            prompt = hit + miss;
        }

        double? total = First(usage, "total_tokens");
        double? completion = First(usage, "completion_tokens") ?? First(usage, "output_tokens");
        if (completion is null && total is double totalValue && prompt is double promptValue)
        {
            completion = Math.Max(0, totalValue - promptValue);
        }

        double? cacheRead = First(usage, CacheReadPaths);
        double? cacheWrite = First(usage, CacheWritePaths);

        if (prompt is 0)
        {
            prompt = null;
        }

        return new Counts(prompt, completion, total, cacheRead, cacheWrite);
    }

    public static double? First(JsonObject usage, params string[] paths)
    {
        foreach (string path in paths)
        {
            if (Resolve(usage, path) is JsonNode node && AnthropicChatJson.Number(node) is double value && value >= 0)
            {
                return Math.Floor(value);
            }
        }

        return null;
    }

    private static JsonNode? Resolve(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (string segment in path.Split('.'))
        {
            node = node is JsonObject obj ? obj[segment] : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }
}
