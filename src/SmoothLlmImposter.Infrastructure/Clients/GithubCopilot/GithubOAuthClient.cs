using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;

/// <summary>
/// Device-flow OAuth and account validation against github.com / api.github.com. Requests never follow a
/// redirect, responses are size-bounded, and no raw OAuth body or authorization header is ever logged. Token
/// polling and refresh are plain calls — retry decisions belong to the login/refresh state machines, so no
/// automatic POST retry policy is attached to this client.
/// </summary>
internal sealed class GithubOAuthClient(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<GithubOAuthClient> logger) : IGithubOAuthClient
{
    internal const string HttpClientName = "github-copilot-oauth";
    internal const string GitHubApiVersion = "2022-11-28";

    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string UserUrl = "https://api.github.com/user";
    private const string CopilotUserUrl = "https://api.github.com/copilot_internal/user";
    private const int MaxResponseBytes = 256 * 1024;

    public async Task<GithubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostFormAsync(
            DeviceCodeUrl,
            [
                new("client_id", clientId),
                new("scope", "read:user")
            ],
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub device-code request failed with HTTP {StatusCode}", (int)response.StatusCode);
            if ((int)response.StatusCode is >= 400 and < 500)
            {
                throw new GithubCopilotClientRejectedException(
                    $"GitHub did not accept clientId '{clientId}' for device flow (HTTP {(int)response.StatusCode}). " +
                    "Check that the client ID belongs to an OAuth application with device flow enabled.");
            }

            throw new GithubCopilotUpstreamException(
                $"GitHub rejected the device-code request with HTTP {(int)response.StatusCode}. Retry shortly; if it persists, check GitHub status.");
        }

        GithubDeviceCodeResponse? body = await ReadJsonAsync(response, GithubCopilotJsonContext.Default.GithubDeviceCodeResponse, cancellationToken);
        if (body is null || string.IsNullOrWhiteSpace(body.DeviceCode) || string.IsNullOrWhiteSpace(body.UserCode))
        {
            throw new GithubCopilotUpstreamException("GitHub returned a malformed device-code response.");
        }

        return new GithubDeviceCode(
            body.DeviceCode,
            body.UserCode,
            body.VerificationUri,
            timeProvider.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(1, body.ExpiresIn)),
            Math.Max(1, body.Interval));
    }

    public async Task<GithubTokenPollResult> PollTokenAsync(string clientId, string deviceCode, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostFormAsync(
            AccessTokenUrl,
            [
                new("client_id", clientId),
                new("device_code", deviceCode),
                new("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            ],
            cancellationToken);

        GithubAccessTokenResponse? body = await ReadJsonAsync(response, GithubCopilotJsonContext.Default.GithubAccessTokenResponse, cancellationToken);
        return ClassifyTokenResponse(response, body);
    }

    public async Task<GithubTokenPollResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostFormAsync(
            AccessTokenUrl,
            [
                new("client_id", clientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken)
            ],
            cancellationToken);

        GithubAccessTokenResponse? body = await ReadJsonAsync(response, GithubCopilotJsonContext.Default.GithubAccessTokenResponse, cancellationToken);
        return ClassifyTokenResponse(response, body);
    }

    public async Task<GithubUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(UserUrl, accessToken, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub /user lookup failed with HTTP {StatusCode}", (int)response.StatusCode);
            return null;
        }

        GithubUserResponse? body = await ReadJsonAsync(response, GithubCopilotJsonContext.Default.GithubUserResponse, cancellationToken);
        return body is null || string.IsNullOrWhiteSpace(body.Login) ? null : new GithubUser(body.Login, body.Id);
    }

    public async Task<GithubCopilotEntitlement?> GetCopilotEntitlementAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(CopilotUserUrl, accessToken, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("GitHub Copilot entitlement lookup failed with HTTP {StatusCode}", (int)response.StatusCode);
            return null;
        }

        GithubCopilotUserResponse? body = await ReadJsonAsync(response, GithubCopilotJsonContext.Default.GithubCopilotUserResponse, cancellationToken);
        if (body?.Endpoints?.Api is not { Length: > 0 } apiOrigin)
        {
            return null;
        }

        return new GithubCopilotEntitlement(body.ChatEnabled, apiOrigin);
    }

    private GithubTokenPollResult ClassifyTokenResponse(HttpResponseMessage response, GithubAccessTokenResponse? body)
    {
        if (body is null)
        {
            throw new GithubCopilotUpstreamException(
                $"The GitHub token endpoint returned an unreadable response (HTTP {(int)response.StatusCode}). Retry the same call; the device grant is unchanged.");
        }

        // A server-side failure (or rate limit) is not a verdict on the grant: surface it as retryable so the
        // login flow keeps the issued device/token state and the refresher keeps a still-valid old pair.
        int status = (int)response.StatusCode;
        if (status >= 500 || status == 429 || status == 408)
        {
            throw new GithubCopilotUpstreamException(
                $"The GitHub token endpoint is unavailable (HTTP {status}). Retry the same call; the device grant is unchanged.");
        }

        if (!string.IsNullOrEmpty(body.AccessToken))
        {
            DateTimeOffset? expiresAt = body.ExpiresIn is int expiresIn && expiresIn > 0
                ? timeProvider.GetUtcNow() + TimeSpan.FromSeconds(expiresIn)
                : null;

            return new GithubTokenPollResult(
                GithubTokenPollKind.Success,
                body.AccessToken,
                body.RefreshToken,
                expiresAt,
                body.Interval);
        }

        GithubTokenPollKind kind = body.Error switch
        {
            "authorization_pending" => GithubTokenPollKind.Pending,
            "slow_down" => GithubTokenPollKind.SlowDown,
            "access_denied" => GithubTokenPollKind.Denied,
            "expired_token" => GithubTokenPollKind.Expired,
            _ => GithubTokenPollKind.Rejected
        };

        logger.LogDebug("GitHub token endpoint classified as {Kind} (HTTP {StatusCode})", kind, (int)response.StatusCode);
        return new GithubTokenPollResult(kind, IntervalSeconds: body.Interval);
    }

    private async Task<HttpResponseMessage> PostFormAsync(string url, (string Key, string Value)[] fields, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(static field => new KeyValuePair<string, string>(field.Key, field.Value)))
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("SmoothLlmImposter/1.0");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string accessToken, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.ParseAdd("SmoothLlmImposter/1.0");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
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
                throw new GithubCopilotUpstreamException("GitHub returned an unexpectedly large response.");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return JsonSerializer.Deserialize(buffer.ToArray(), typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
