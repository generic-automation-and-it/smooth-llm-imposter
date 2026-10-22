using System.Net.Http.Headers;
using System.Text.Json;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Domain.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;

/// <summary>
/// Uncached model discovery against the account's validated Copilot origin. Parsing is <see cref="JsonDocument"/>
/// based — no reflection — so the path stays NativeAOT-safe. Endpoint support is normalized to the two surfaces
/// this proxy serves (<c>messages</c>, <c>responses</c>); Chat is never a setup target.
/// </summary>
internal sealed class GithubCopilotModelsClient(IHttpClientFactory httpClientFactory)
    : IGithubCopilotModelsClient
{
    internal const string HttpClientName = "github-copilot-api";

    private const int MaxResponseBytes = 1024 * 1024;
    private const string EndpointMessages = "messages";
    private const string EndpointResponses = "responses";

    public async Task<GithubCopilotModel[]> ListModelsAsync(string accessToken, string apiOrigin, CancellationToken cancellationToken)
    {
        if (!GithubCopilotTransportPolicy.TryParseAllowedOrigin(apiOrigin, out Uri origin))
        {
            throw new GithubCopilotUpstreamException("The connection's Copilot API origin is not an allowed GitHub-controlled HTTPS origin.");
        }

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.GetLeftPart(UriPartial.Authority) + "/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(GithubCopilotTransportPolicy.ApiVersionHeader, GithubCopilotTransportPolicy.ApiVersionValue);
        request.Headers.UserAgent.ParseAdd(GithubCopilotTransportPolicy.UserAgentValue);

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new GithubCopilotUpstreamException(
                $"Model discovery returned HTTP {(int)response.StatusCode} from the Copilot API.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new GithubCopilotUpstreamException("Copilot model discovery returned an unexpectedly large response.");
            }

            buffer.Write(chunk, 0, read);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        JsonElement root = document.RootElement;

        // The catalog is published either as {"data":[...]} or as a bare array depending on account policy.
        JsonElement models = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array
                ? data
                : default;

        if (models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<GithubCopilotModel>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            if (!model.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string[] endpoints = ReadEndpoints(model, out bool advertised);
            if (!advertised)
            {
                // Only an absent advertisement falls back to both surfaces. A model that advertises endpoints
                // none of which this proxy serves (e.g. chat-only) must not be offered for Messages/Responses.
                endpoints = [EndpointMessages, EndpointResponses];
            }

            results.Add(new GithubCopilotModel(
                idElement.GetString()!,
                endpoints,
                ReadPolicy(model)));
        }

        return results.ToArray();
    }

    private static string[] ReadEndpoints(JsonElement model, out bool advertised)
    {
        advertised = false;
        if (!model.TryGetProperty("supported_endpoints", out JsonElement endpoints) ||
            endpoints.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        advertised = true;

        var normalized = new List<string>();
        foreach (JsonElement endpoint in endpoints.EnumerateArray())
        {
            if (endpoint.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? surface = NormalizeEndpoint(endpoint.GetString()!);
            if (surface is not null && !normalized.Contains(surface))
            {
                normalized.Add(surface);
            }
        }

        return normalized.ToArray();
    }

    private static string? NormalizeEndpoint(string path) =>
        path switch
        {
            "/v1/messages" or "/messages" => EndpointMessages,
            "/responses" or "/v1/responses" => EndpointResponses,
            _ => null
        };

    private static string? ReadPolicy(JsonElement model)
    {
        if (!model.TryGetProperty("policy", out JsonElement policy))
        {
            return null;
        }

        return policy.ValueKind switch
        {
            JsonValueKind.String => policy.GetString(),
            JsonValueKind.Object when policy.TryGetProperty("state", out JsonElement state) && state.ValueKind == JsonValueKind.String
                => state.GetString(),
            _ => null
        };
    }
}
