using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

internal sealed class DecisionModelClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ImposterOptions> options) : IDecisionModelClient, IDisposable
{
    internal const string HttpClientName = "decisions-tools";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken)
    {
        DecisionsToolsOptions settings = options.Value.DecisionsTools;
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(settings.TimeoutSeconds), cancellationToken))
        {
            throw new TimeoutException("The decision service concurrency gate timed out.");
        }

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(HttpClientName);
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                settings.BaseUrl.TrimEnd('/') + settings.Path)
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json")
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
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
