using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polly.Timeout;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

internal sealed class DecisionModelClient(
    IHttpClientFactory httpClientFactory,
    DecisionServiceProfiles profiles,
    DecisionServiceGate gate) : IDecisionModelClient
{
    internal const string HttpClientName = "decision-service";

    // The section's attempt budget travels on the request: one named client serves every section, so the
    // resilience pipeline reads these back instead of baking one profile in (HLD 015 LADR-05).
    internal static readonly HttpRequestOptionsKey<int> MaxAttemptsKey = new("smoothllmimposter-decision-max-attempts");
    internal static readonly HttpRequestOptionsKey<int> TimeoutSecondsKey = new("smoothllmimposter-decision-timeout-seconds");

    public async Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken)
    {
        DecisionServiceOptions settings = profiles.Resolve(section);
        using DecisionServiceGate.Admission admission =
            await gate.EnterAsync(settings, TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken);

        using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
        {
            Content = new StringContent(
                WithDefaultModel(request, settings.Model).ToJsonString(), Encoding.UTF8, "application/json")
        };
        message.Options.Set(MaxAttemptsKey, settings.MaxAttempts);
        message.Options.Set(TimeoutSecondsKey, settings.TimeoutSeconds);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TimeoutRejectedException timeout)
        {
            throw new DecisionServiceTimeoutException(
                "The decision service attempt timed out.", timeout);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(body) is not JsonObject envelope || envelope["answers"] is not JsonObject)
            {
                throw new JsonException("The decision service returned an invalid answers envelope.");
            }

            return envelope;
        }
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
