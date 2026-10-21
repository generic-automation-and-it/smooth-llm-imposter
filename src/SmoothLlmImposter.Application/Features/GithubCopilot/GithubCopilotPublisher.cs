using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>Outcome of publishing one saved connection revision. A failure leaves the durable row intact (D2).</summary>
public sealed record GithubCopilotPublication(bool Published, string? FailureMessage);

/// <summary>
/// Publishes a saved connection revision into the routing seam: it materializes any missing provider pair
/// (appending it without reordering existing providers), then replaces the in-memory connection snapshot both
/// protocol profiles read. Publication is idempotent for a revision: an existing key keeps its Models, Enabled,
/// Name, session/timeout settings and order, while the managed identity (origin, Bearer scheme, header) is
/// rewritten to the connection's values. Validation and registry mutation happen as one batch (D2/D1).
/// </summary>
public interface IGithubCopilotPublisher
{
    GithubCopilotPublication Publish(GithubCopilotConnection connection);
}

internal sealed class GithubCopilotPublisher(
    IProviderRegistry registry,
    IGithubCopilotConnectionRegistry connections,
    IGithubCopilotSecretProtector protector,
    ILogger<GithubCopilotPublisher> logger) : IGithubCopilotPublisher
{
    private const string BearerScheme = "Bearer";
    private const string ResponsesApi = "responses";
    private const string NoNormalization = "none";

    public GithubCopilotPublication Publish(GithubCopilotConnection connection)
    {
        string[] providerKeys =
        [
            GithubCopilotProfileKey.Key(connection.Prefix, ApiDialect.Anthropic),
            GithubCopilotProfileKey.Key(connection.Prefix, ApiDialect.OpenAi)
        ];

        if (!GithubCopilotTransportPolicy.TryParseAllowedOrigin(connection.ApiOrigin, out Uri origin))
        {
            return new GithubCopilotPublication(
                Published: false,
                $"The saved API origin '{connection.ApiOrigin}' is not an allowed GitHub Copilot HTTPS origin.");
        }

        string accessToken;
        string? refreshToken;
        try
        {
            accessToken = protector.Unprotect(connection.AccessTokenCiphertext);
            refreshToken = string.IsNullOrEmpty(connection.RefreshTokenCiphertext)
                ? null
                : protector.Unprotect(connection.RefreshTokenCiphertext);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not decrypt the saved GitHub Copilot connection for prefix {Prefix}", connection.Prefix);
            return new GithubCopilotPublication(
                Published: false,
                "The saved credential could not be decrypted with the current key ring. Restore the Copilot key ring volume or start a new login.");
        }

        Dictionary<string, ProviderOptions> proposed = ProviderOptionsCloner.CloneDictionary(registry.Snapshot());
        var mutations = new Dictionary<string, ProviderOptions>(StringComparer.Ordinal);
        foreach (string key in providerKeys)
        {
            string dialect = key == providerKeys[0] ? ApiDialect.Anthropic.ToToken() : ApiDialect.OpenAi.ToToken();
            ProviderOptions materialized = proposed.TryGetValue(key, out ProviderOptions? existing)
                ? Materialize(existing, dialect, origin)
                : Create(dialect, origin);
            proposed[key] = materialized;
            mutations[key] = materialized;
        }

        try
        {
            ProviderConfigurationValidation.EnsureValidRegistry(proposed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refused to publish GitHub Copilot pair for prefix {Prefix}: the registry would be invalid", connection.Prefix);
            return new GithubCopilotPublication(Published: false, ex.Message);
        }

        registry.ApplyBatch(mutations);

        DateTimeOffset? expiresAt = null;
        if (!string.IsNullOrEmpty(connection.ExpiresAtUtc) &&
            DateTimeOffset.TryParse(connection.ExpiresAtUtc, out DateTimeOffset parsedExpiry))
        {
            expiresAt = parsedExpiry;
        }

        connections.Publish(new GithubCopilotPublishedConnection(
            connection.Prefix,
            connection.ConnectionId,
            connection.ClientId,
            origin.GetLeftPart(UriPartial.Authority),
            accessToken,
            refreshToken,
            expiresAt,
            connection.Revision,
            GithubCopilotConnectionState.Active));

        logger.LogInformation(
            "Published GitHub Copilot connection {ConnectionId} for prefix {Prefix} at revision {Revision}",
            connection.ConnectionId,
            string.IsNullOrEmpty(connection.Prefix) ? "(unprefixed)" : connection.Prefix,
            connection.Revision);

        return new GithubCopilotPublication(Published: true, FailureMessage: null);
    }

    // Existing pair definitions keep every operator-editable value; only the managed identity is rewritten.
    private static ProviderOptions Materialize(ProviderOptions existing, string dialect, Uri origin)
    {
        ProviderOptions clone = ProviderOptionsCloner.Clone(existing);
        clone.Dialect = dialect;
        clone.BaseUrl = origin.GetLeftPart(UriPartial.Authority);
        clone.Secret = null;
        clone.AuthScheme = BearerScheme;
        clone.AuthHeader = null;
        clone.IsDefault = false;
        clone.OpenAiUpstreamApi = ResponsesApi;
        clone.RequestNormalization = NoNormalization;
        clone.StripEncryptedContent = null;
        return clone;
    }

    private static ProviderOptions Create(string dialect, Uri origin) => new()
    {
        Dialect = dialect,
        BaseUrl = origin.GetLeftPart(UriPartial.Authority),
        Secret = null,
        AuthScheme = BearerScheme,
        AuthHeader = null,
        IsDefault = false,
        Enabled = true,
        OpenAiUpstreamApi = ResponsesApi,
        RequestNormalization = NoNormalization,
        Models = []
    };
}
