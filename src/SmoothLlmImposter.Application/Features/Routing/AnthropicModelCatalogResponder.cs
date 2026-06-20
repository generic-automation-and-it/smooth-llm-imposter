using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Synthesizes the Anthropic <c>GET /v1/models</c> response from the configured catalogue (HLD 005,
/// Anthropic scope). The advertised <c>id</c> values are the distinct <c>to</c> targets across every
/// Anthropic-dialect provider mapping (LADR-01); duplicates collapse to one entry preserving catalogue
/// order. The body is built from configuration alone — no upstream call, no credential read (LADR-02).
/// </summary>
/// <remarks>
/// The Anthropic List Models schema is NOT the OpenAI one: the envelope has no <c>object: "list"</c> and
/// carries pagination fields (<c>first_id</c>/<c>has_more</c>/<c>last_id</c>); each model is a
/// <c>ModelInfo</c> with <c>type: "model"</c> (not <c>object</c>), a <c>display_name</c>, and an RFC 3339
/// <c>created_at</c> string (not an integer <c>created</c>). Config has no truthful value for the richer
/// <c>capabilities</c>/<c>max_input_tokens</c>/<c>max_tokens</c> fields, so the minimal ModelInfo is emitted
/// (issue #28). <c>created_at</c> is a fixed constant so the response is byte-identical across calls (NFR-01);
/// only <c>to</c> ids ever enter the body, never a <c>Secret</c> (NFR-04).
/// </remarks>
internal sealed class AnthropicModelCatalogResponder : IAnthropicModelCatalogResponder
{
    // Fixed, configuration-independent release date. Never wall-clock — a time-derived value would break
    // the determinism guarantee (NFR-01). The Anthropic docs note created_at "may be set to an epoch value
    // if the release date is unknown", which is exactly our case: the router does not know release dates.
    private const string EpochCreatedAt = "1970-01-01T00:00:00Z";
    private const string ModelType = "model";

    private readonly IProviderCatalog _catalog;

    public AnthropicModelCatalogResponder(IProviderCatalog catalog) => _catalog = catalog;

    public string BuildModelsResponse()
    {
        var data = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ProviderRoute provider in _catalog.ProvidersFor(ApiDialect.Anthropic))
        {
            foreach (ModelMapping mapping in provider.Models)
            {
                // Distinct on the upstream target id, case-sensitive, first occurrence wins so output order
                // is a deterministic function of catalogue order (NFR-01). Passthrough/default providers have
                // no Models[] and therefore contribute nothing.
                if (seen.Add(mapping.To))
                {
                    data.Add(ModelInfo(mapping.To));
                }
            }
        }

        string? firstId = data.Count > 0 ? ((string)data[0]!["id"]!) : null;
        string? lastId = data.Count > 0 ? ((string)data[^1]!["id"]!) : null;

        var envelope = new JsonObject
        {
            ["data"] = data,
            ["first_id"] = firstId,
            ["has_more"] = false,
            ["last_id"] = lastId,
        };

        return envelope.ToJsonString();
    }

    private static JsonObject ModelInfo(string id) => new()
    {
        ["id"] = id,
        ["type"] = ModelType,
        // No real display name exists in config; the to id verbatim keeps the response deterministic (issue #28).
        ["display_name"] = id,
        ["created_at"] = EpochCreatedAt,
    };
}
