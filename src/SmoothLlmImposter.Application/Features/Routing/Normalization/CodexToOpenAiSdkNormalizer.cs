using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Normalization;

/// <summary>
/// v1 Codex → OpenAI-SDK normalization: keep only the tools a strict OpenAI-compatible upstream
/// accepts, so a vanilla Codex client works against e.g. <c>opencode-go</c> (kimi). Derived from the
/// empirically-observed upstream contract (HLD 004 <c>examples/upstream-tool-validation.md</c>):
/// <list type="bullet">
///   <item><c>tools[].type</c> must be <c>function</c> or <c>plugin</c>; every other type
///   (<c>custom</c>, <c>web_search</c>, <c>image_generation</c>, <c>tool_search</c>, …) is rejected
///   with <c>unknown tool type</c>. A <c>namespace</c> wrapper is rejected too — but its nested
///   <c>function</c> tools are valid, so it is <b>flattened</b> (capability-preserving) rather than
///   dropped, which is what makes the Codex GitHub connector's ~80 tools survive.</item>
///   <item><c>function.name</c> must match <c>^[A-Za-z_][A-Za-z0-9_-]*$</c> — non-empty, no dots, no
///   leading digit; a leading <c>_</c> is tolerated. Names that fail are dropped.</item>
/// </list>
/// <para>
/// <b>Removal, not rename</b> (LADR-02): a tool that is never sent is never echoed back, so the
/// transform stays request-only — no response rewrite. The capability cost is that a dropped tool is
/// unavailable for that request. Prior-turn <c>function_call</c>/<c>function_call_output</c> history
/// for a now-dropped tool is left untouched in <c>messages</c>; v1 only filters <c>tools[]</c>.
/// </para>
/// <para>
/// Handles both tool shapes — flat Responses (<c>{type,name,parameters}</c>) and nested Chat
/// (<c>{type:"function",function:{name,…}}</c>) — because it runs before the Responses→Chat conversion
/// for chat upstreams and not at all for responses upstreams. Idempotent: re-running on an
/// already-normalized body is a no-op (NFR-02).
/// </para>
/// </summary>
internal sealed partial class CodexToOpenAiSdkNormalizer : IRequestNormalizer
{
    public RequestNormalization Kind => RequestNormalization.CodexToOpenAiSdk;

    public void Normalize(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools)
        {
            return;
        }

        var survivors = new JsonArray();
        var survivingNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (JsonNode? node in tools)
        {
            AppendNormalized(node, survivors, survivingNames);
        }

        if (survivors.Count == 0)
        {
            // No valid tool survived — drop the (now-meaningless) tools/tool_choice entirely. An absent
            // tools array is accepted upstream (baseline), an empty array is not guaranteed to be.
            root.Remove("tools");
            root.Remove("tool_choice");
            return;
        }

        root["tools"] = survivors;
        CleanToolChoice(root, survivingNames);
    }

    private static void AppendNormalized(JsonNode? node, JsonArray survivors, HashSet<string> survivingNames)
    {
        if (node is not JsonObject tool)
        {
            return;
        }

        string? type = EffectiveType(tool);

        switch (type)
        {
            case "namespace":
                // Flatten the wrapper into its nested tools; each inner tool is re-validated.
                if (tool["tools"] is JsonArray inner)
                {
                    foreach (JsonNode? innerNode in inner)
                    {
                        AppendNormalized(innerNode, survivors, survivingNames);
                    }
                }

                return;

            case "plugin":
                // Listed as supported by the upstream contract; keep verbatim (schema uncharacterized).
                survivors.Add(tool.DeepClone());
                return;

            case "function":
                string? name = FunctionName(tool);
                if (name is not null && ValidNameRegex().IsMatch(name))
                {
                    survivors.Add(tool.DeepClone());
                    survivingNames.Add(name);
                }

                // Empty/dotted/leading-digit names are dropped (request-only — no rename).
                return;

            default:
                // Unknown/unsupported tool type (custom, web_search, image_generation, tool_search, …) — drop.
                return;
        }
    }

    // Tool_choice that names a tool no longer present would 400 (or pin a dropped tool); remove it so the
    // upstream falls back to its default selection. String forms ("auto"/"none"/"required") are untouched.
    private static void CleanToolChoice(JsonObject root, HashSet<string> survivingNames)
    {
        if (root["tool_choice"] is not JsonObject choice)
        {
            return;
        }

        string? name = FunctionName(choice);
        if (name is not null && !survivingNames.Contains(name))
        {
            root.Remove("tool_choice");
        }
    }

    private static string? EffectiveType(JsonObject tool)
    {
        if (tool["type"] is JsonValue typeValue && typeValue.GetValueKind() == JsonValueKind.String)
        {
            return typeValue.GetValue<string>().Trim().ToLowerInvariant();
        }

        // Tolerate a type-less entry by inferring shape: a tools[] array ⇒ namespace wrapper; a function
        // object or a bare name ⇒ function. Anything else is unrecognised and dropped by the caller.
        if (tool["tools"] is JsonArray)
        {
            return "namespace";
        }

        return tool["function"] is JsonObject || tool["name"] is not null ? "function" : null;
    }

    // Reads the function name from either tool shape: nested Chat ({function:{name}}) or flat Responses ({name}).
    private static string? FunctionName(JsonObject tool)
    {
        if (tool["function"] is JsonObject function &&
            function["name"] is JsonValue nested &&
            nested.GetValueKind() == JsonValueKind.String)
        {
            return nested.GetValue<string>();
        }

        if (tool["name"] is JsonValue flat && flat.GetValueKind() == JsonValueKind.String)
        {
            return flat.GetValue<string>();
        }

        return null;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex ValidNameRegex();
}
