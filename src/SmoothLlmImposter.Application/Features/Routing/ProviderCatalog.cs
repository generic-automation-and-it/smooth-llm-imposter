using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Materialises <see cref="ImposterOptions"/> into immutable <see cref="ProviderRoute"/> domain objects,
/// indexed by dialect. Validation of the options happens at startup (see ImposterOptionsValidator);
/// this type assumes already-valid options.
/// </summary>
internal sealed class ProviderCatalog : IProviderCatalog
{
    private readonly Dictionary<ApiDialect, List<ProviderRoute>> _byDialect = new();

    public ProviderCatalog(IOptionsSnapshot<ImposterOptions> options)
    {
        foreach ((string key, ProviderOptions provider) in options.Value.Providers)
        {
            ApiDialect dialect = ApiDialectParser.Parse(provider.Dialect);
            CredentialAuthSchemeParser.TryParse(provider.AuthScheme, out CredentialAuthScheme? authScheme);
            OpenAiUpstreamApi upstreamApi = OpenAiUpstreamApiParser.Parse(provider.OpenAiUpstreamApi);

            // Provider identity is the dictionary key; Name is an optional display override (HLD 007).
            string routeName = string.IsNullOrWhiteSpace(provider.Name) ? key : provider.Name;

            var route = new ProviderRoute(
                routeName,
                dialect,
                new Uri(provider.BaseUrl, UriKind.Absolute),
                provider.Secret,
                provider.IsDefault,
                provider.AnthropicVersion,
                provider.Models
                    .Select(m => new ModelMapping(m.From, m.To, m.Caching))
                    .ToArray(),
                upstreamApi,
                authScheme,
                ResolveNormalization(provider.RequestNormalization, upstreamApi),
                provider.Enabled);

            if (!_byDialect.TryGetValue(dialect, out List<ProviderRoute>? routes))
            {
                routes = [];
                _byDialect[dialect] = routes;
            }

            routes.Add(route);
        }
    }

    // Normalization is the generic OpenAI Chat Completions tool contract, not an upstream-specific quirk:
    // any chat_completions upstream rejects Codex's Responses-dialect tool catalog the same way. So it is
    // ON by default for chat_completions (unless explicitly set, including to "none"), and never inferred
    // for a responses upstream — there the Responses tool types are valid and must be preserved. An
    // explicit value always wins; the validator forbids an explicit codex profile outside chat_completions.
    private static RequestNormalization ResolveNormalization(string? configured, OpenAiUpstreamApi upstreamApi) =>
        string.IsNullOrWhiteSpace(configured)
            ? upstreamApi == OpenAiUpstreamApi.ChatCompletions
                ? RequestNormalization.CodexToOpenAiSdk
                : RequestNormalization.None
            : RequestNormalizationParser.Parse(configured);

    // Disabled providers are invisible to every resolution path — imposter matching, default passthrough,
    // and the local /v1/models catalogue (HLD 008 LADR-03). Filtering here is the single source of truth, so
    // downstream consumers (resolver, model responders) never surface a disabled provider.
    public IReadOnlyList<ProviderRoute> ProvidersFor(ApiDialect dialect) =>
        _byDialect.TryGetValue(dialect, out List<ProviderRoute>? routes)
            ? routes.Where(static route => route.Enabled).ToArray()
            : [];
}
