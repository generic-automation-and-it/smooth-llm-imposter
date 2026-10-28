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
            logger.LogDebug("No optimization strategy enabled — forwarding without intercept");
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

        string optimized = Merge(request.Body, results);

        // Rough token estimate (chars / 4) for operator visibility; not a tokenizer result.
        int savedChars = request.Body.Length - optimized.Length;
        int savedTokens = savedChars > 0 ? savedChars / 4 : 0;
        logger.LogInformation(
            "LLM optimization complete: {OriginalChars} -> {OptimizedChars} chars (~{SavedTokens} tokens saved)",
            request.Body.Length, optimized.Length, savedTokens);

        return optimized;
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
            // Type only: exception messages can carry request text (NFR-04).
            logger.LogWarning(
                "Optimization strategy {Strategy} failed ({Failure}); contributing no edit",
                strategy.Name, failure.GetType().Name);
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

        if (all.Any(static patch => !patch.Pointer.StartsWith('/')))
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

        List<OptimizationPatch> nested = all.Where(static patch => Segments(patch.Pointer).Length > 1).ToList();
        List<OptimizationPatch> topLevel = all.Where(static patch => Segments(patch.Pointer).Length == 1).ToList();

        string updated = originalBody;
        if (nested.Count > 0 &&
            !JsonPointerPatcher.TryReplace(originalBody, nested, out updated))
        {
            return originalBody;
        }

        if (topLevel.Count == 0)
        {
            return updated;
        }

        JsonObject root;
        try
        {
            root = JsonNodeMaterializer.ParseObject(updated);
        }
        catch
        {
            return originalBody;
        }

        var replacements = new JsonObject();
        var names = new List<string>();
        foreach (OptimizationPatch patch in topLevel)
        {
            string name = Unescape(Segments(patch.Pointer)[0]);
            if (!root.ContainsKey(name))
            {
                return originalBody;
            }
            names.Add(name);
            replacements[name] = patch.Replacement?.DeepClone();
        }

        return Decisions.DecisionJson.ReplaceTopLevel(updated, replacements, names.ToArray());
    }

    private static string[] Segments(string pointer) =>
        pointer.StartsWith('/')
            ? pointer.Substring(1).Split('/', StringSplitOptions.None)
            : throw new InvalidOperationException("Optimization pointer must start with '/'.");

    private static bool Overlaps(string a, string b)
    {
        if (a.Equals(b, StringComparison.Ordinal))
        {
            return true;
        }
        // Compare segment-wise so "/arr/01" and "/arr/1" are recognised as the same slot.
        string[] aSegs = a.Split('/', StringSplitOptions.None);
        string[] bSegs = b.Split('/', StringSplitOptions.None);
        int common = Math.Min(aSegs.Length, bSegs.Length);
        for (int i = 0; i < common; i++)
        {
            if (!SegmentsEqual(aSegs[i], bSegs[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SegmentsEqual(string x, string y) =>
        string.Equals(NormalizeIndex(x), NormalizeIndex(y), StringComparison.Ordinal);

    private static string NormalizeIndex(string segment) =>
        int.TryParse(segment, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : segment;

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/").Replace("~0", "~");
}