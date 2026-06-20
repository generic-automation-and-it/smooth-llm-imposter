using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Answers <c>GET /openai/v1/models</c> from configuration: the distinct union of every <c>to</c> target
/// across the OpenAI-dialect route catalogue, shaped as an OpenAI <c>ListModelsResponse</c>. The list is a
/// projection of the router's own configuration, not a relay of an upstream's catalogue — so it issues no
/// upstream request and reads no credential store (HLD 005, LADR-02). Default/passthrough providers carry
/// no <c>Models[]</c>, so they contribute nothing.
/// </summary>
internal sealed class OpenAiModelCatalogResponder : IModelCatalogResponder
{
    // created is a fixed, configuration-independent constant — never wall-clock — so two calls under one
    // config are byte-identical (NFR-01). OpenAI's schema only requires the field to be a present integer.
    private const long CreatedConstant = 0;

    private readonly IProviderCatalog _catalog;

    public OpenAiModelCatalogResponder(IProviderCatalog catalog) => _catalog = catalog;

    public string BuildOpenAiModelsResponse()
    {
        var data = new JsonArray();

        // Ordinal dedup preserving catalogue order: model ids are case-sensitive identifiers, and on a
        // duplicate `to` the first declaring provider (catalogue order) supplies owned_by — matching the
        // routing first-match-wins convention (LADR-01, LADR-04).
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ProviderRoute provider in _catalog.ProvidersFor(ApiDialect.OpenAi))
        {
            foreach (ModelMapping mapping in provider.Models)
            {
                if (!seen.Add(mapping.To))
                {
                    continue;
                }

                data.Add(new JsonObject
                {
                    ["id"] = mapping.To,
                    ["object"] = "model",
                    ["created"] = CreatedConstant,
                    ["owned_by"] = provider.Name,
                });
            }
        }

        // Never serialize a Secret/BaseUrl/AuthScheme — only the `to` id and provider name reach the body (NFR-04).
        var envelope = new JsonObject
        {
            ["object"] = "list",
            ["data"] = data,
        };

        return envelope.ToJsonString();
    }
}
