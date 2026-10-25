using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

internal sealed class OptimizationCoordinator(
    IEnumerable<IOptimizationStrategy> strategies,
    ILogger<OptimizationCoordinator> logger) : IOptimizationCoordinator
{
    public async Task<string> OptimizeAsync(OptimizationRequest request, CancellationToken cancellationToken)
    {
        IOptimizationStrategy[] enabled = strategies.Where(s => s.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return request.Body;
        }

        Task<OptimizationEdit>[] tasks = enabled
            .Select(strategy => RunOneAsync(strategy, request, cancellationToken))
            .ToArray();

        OptimizationEdit[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        return Merge(request.Body, results);
    }

    private async Task<OptimizationEdit> RunOneAsync(
        IOptimizationStrategy strategy, OptimizationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await strategy.OptimizeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Optimization strategy {Strategy} failed; contributing no edit", strategy.Name);
            return OptimizationEdit.None;
        }
    }

    private static string Merge(string originalBody, IReadOnlyList<OptimizationEdit> edits)
    {
        List<OptimizationPatch> all = edits
            .Where(e => e?.Patches is not null)
            .SelectMany(e => e.Patches!)
            .Where(p => p is not null && !string.IsNullOrEmpty(p.Pointer))
            .ToList();
        if (all.Count == 0)
        {
            return originalBody;
        }

        // Overlap: the same pointer, or an ancestor/descendant of it, claimed by more than one patch.
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                if (Overlaps(all[i].Pointer, all[j].Pointer))
                {
                    return originalBody;
                }
            }
        }

        JsonObject root;
        try
        {
            root = JsonNodeMaterializer.ParseObject(originalBody);
        }
        catch
        {
            return originalBody;
        }

        foreach (OptimizationPatch patch in all)
        {
            if (!TrySet(root, patch.Pointer, patch.Replacement))
            {
                return originalBody;
            }
        }

        return root.ToJsonString();
    }

    private static bool TrySet(JsonObject root, string pointer, JsonNode? value)
    {
        if (string.IsNullOrEmpty(pointer) || !pointer.StartsWith('/'))
        {
            return false;
        }

        string[] segments = pointer.Substring(1).Split('/', StringSplitOptions.None)
            .Select(Unescape).ToArray();

        JsonNode? current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Navigate(current, segments[i]);
            if (current is null)
            {
                return false;
            }
        }

        string last = segments[^1];
        JsonNode? replacement = value?.DeepClone();

        if (current is JsonObject obj)
        {
            if (!obj.ContainsKey(last))
            {
                return false;
            }
            obj[last] = replacement;
            return true;
        }

        if (current is JsonArray arr &&
            int.TryParse(last, out int index) &&
            index >= 0 && index < arr.Count)
        {
            arr[index] = replacement;
            return true;
        }

        return false;
    }

    private static JsonNode? Navigate(JsonNode? node, string segment)
    {
        if (node is JsonObject obj)
        {
            return obj[segment];
        }

        if (node is JsonArray arr &&
            int.TryParse(segment, out int index) &&
            index >= 0 && index < arr.Count)
        {
            return arr[index];
        }

        return null;
    }

    private static bool Overlaps(string a, string b) =>
        a.Equals(b, StringComparison.Ordinal) ||
        a.StartsWith(b + "/", StringComparison.Ordinal) ||
        b.StartsWith(a + "/", StringComparison.Ordinal);

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/").Replace("~0", "~");
}