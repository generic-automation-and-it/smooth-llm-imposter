using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

internal sealed class DecisionModelClient(
    IHttpClientFactory httpClientFactory,
    IOptions<DecisionServiceOptions> options,
    DecisionServiceGate gate) : IDecisionModelClient
{
    internal const string HttpClientName = "decision-service";

    public async Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken)
    {
        DecisionServiceOptions settings = options.Value;
        using DecisionServiceGate.Admission admission =
            await gate.EnterAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            settings.BaseUrl.TrimEnd('/') + settings.Path)
        {
            Content = new StringContent(
                WithDefaultModel(request, settings.Model).ToJsonString(), Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await client.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (JsonNode.Parse(body) is not JsonObject envelope || envelope["answers"] is not JsonObject)
        {
            throw new JsonException("The decision service returned an invalid answers envelope.");
        }

        return envelope;
    }

    // Model selection is request data; the integration supplies its default only when a consumer omits it,
    // on a copy so the caller's request is never mutated (HLD 015 LADR-01, NFR-03).
    private static JsonObject WithDefaultModel(JsonObject request, string model)
    {
        if (request.TryGetPropertyValue("model", out JsonNode? existing)
            && existing is not null
            && !(existing is JsonValue existingValue && existingValue.GetValueKind() == JsonValueKind.Null))
        {
            return request;
        }

        JsonObject withModel = request.DeepClone().AsObject();
        withModel["model"] = model;
        return withModel;
    }
}
