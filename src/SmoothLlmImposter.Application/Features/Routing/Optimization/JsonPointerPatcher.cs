using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Replaces nested JSON values in place, leaving every untouched byte of the original body intact
/// (HLD 017 LADR-08 / NFR-01). Only value replacement is supported; removals are handled at the
/// top level by <see cref="Decisions.DecisionJson.ReplaceTopLevel"/>.
/// </summary>
internal static class JsonPointerPatcher
{
    internal static bool TryReplace(
        string body,
        IReadOnlyList<OptimizationPatch> patches,
        out string result)
    {
        result = body;
        if (patches.Count == 0)
        {
            return true;
        }

        if (patches.Any(static patch => patch.Replacement is null))
        {
            return false;
        }

        HashSet<string> targets = patches.Select(static patch => patch.Pointer).ToHashSet(StringComparer.Ordinal);
        if (targets.Count != patches.Count)
        {
            return false;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(body);
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read())
        {
            return false;
        }

        var path = new List<string>();
        var spans = new Dictionary<string, (int Start, int End)>(StringComparer.Ordinal);
        Scan(ref reader, path, targets, spans);
        if (spans.Count != patches.Count)
        {
            return false;
        }

        var builder = new StringBuilder(body);
        foreach (OptimizationPatch patch in patches.OrderByDescending(p => spans[p.Pointer].Start))
        {
            (int start, int end) = spans[patch.Pointer];
            builder.Remove(start, end - start).Insert(start, patch.Replacement!.ToJsonString());
        }

        result = builder.ToString();
        return true;
    }

    private static void Scan(
        ref Utf8JsonReader reader,
        List<string> path,
        IReadOnlySet<string> targets,
        Dictionary<string, (int Start, int End)> spans)
    {
        int start = (int)reader.TokenStartIndex;
        string pointer = "/" + string.Join("/", path.Select(Escape));
        if (targets.Contains(pointer))
        {
            spans[pointer] = (start, SkipValue(ref reader));
            return;
        }

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                reader.Read();
                while (reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString()!;
                    reader.Read();
                    path.Add(name);
                    Scan(ref reader, path, targets, spans);
                    path.RemoveAt(path.Count - 1);
                    reader.Read();
                }
                break;

            case JsonTokenType.StartArray:
                reader.Read();
                int index = 0;
                while (reader.TokenType != JsonTokenType.EndArray)
                {
                    path.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Scan(ref reader, path, targets, spans);
                    path.RemoveAt(path.Count - 1);
                    reader.Read();
                    index++;
                }
                break;
        }
    }

    private static int SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
        return (int)reader.BytesConsumed;
    }

    private static string Escape(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
