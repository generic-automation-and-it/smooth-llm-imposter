using System.Text.Json.Serialization;

namespace SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;

/// <summary>
/// Source-generated JSON metadata for the GitHub/Copilot DTOs this feature exchanges. NativeAOT ships no
/// reflection fallback, so every outbound response shape is registered here rather than parsed reflectively.
/// </summary>
[JsonSerializable(typeof(GithubDeviceCodeResponse))]
[JsonSerializable(typeof(GithubAccessTokenResponse))]
[JsonSerializable(typeof(GithubUserResponse))]
[JsonSerializable(typeof(GithubCopilotUserResponse))]
internal partial class GithubCopilotJsonContext : JsonSerializerContext;

internal sealed record GithubDeviceCodeResponse(
    [property: JsonPropertyName("device_code")] string DeviceCode,
    [property: JsonPropertyName("user_code")] string UserCode,
    [property: JsonPropertyName("verification_uri")] string VerificationUri,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("interval")] int Interval);

internal sealed record GithubAccessTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription,
    [property: JsonPropertyName("interval")] int? Interval,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

internal sealed record GithubUserResponse(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("id")] long Id);

internal sealed record GithubCopilotUserResponse(
    [property: JsonPropertyName("chat_enabled")] bool ChatEnabled,
    [property: JsonPropertyName("endpoints")] GithubCopilotEndpoints? Endpoints);

internal sealed record GithubCopilotEndpoints(
    [property: JsonPropertyName("api")] string? Api);
