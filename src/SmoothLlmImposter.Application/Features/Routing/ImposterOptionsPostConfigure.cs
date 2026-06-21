using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Layers the conventional per-provider environment surface (HLD 007, LADR-02/03) onto the bound
/// <see cref="ImposterOptions"/>. For a provider keyed <c>opencode-go</c> the env prefix is
/// <c>OPENCODE_GO_</c>, and conventional suffixes map onto the provider's scalar fields
/// (<c>OPENCODE_GO_API_KEY</c> → <see cref="ProviderOptions.Secret"/>, etc.). The secret accepts a second,
/// auth-typed spelling — <c>_AUTHORIZATION_BEARER</c> (e.g. <c>ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER</c>
/// for a Bearer subscription token) — that also fills <see cref="ProviderOptions.Secret"/>; <c>_API_KEY</c>
/// is canonical and wins if both are set. Dialect-suffixed sibling
/// providers can share the base provider's secret convention when both are configured
/// (<c>openrouter-openai</c> and <c>openrouter-anthropic</c> may use <c>OPENROUTER_API_KEY</c>, while
/// retaining their own bound <c>AuthScheme</c>).
/// <para>
/// Runs as an <see cref="IPostConfigureOptions{TOptions}"/>, so it executes after the binder and
/// <b>before</b> <c>IValidateOptions</c> at <c>Get</c>/<c>ValidateOnStart</c> — a conventionally-supplied
/// <c>Dialect</c>/<c>BaseUrl</c> is therefore in place before validation. Precedence is fixed
/// (highest wins): conventional env &gt; structured env (<c>Imposter__Providers__&lt;name&gt;__Field</c>)
/// &gt; appsettings. The structured-vs-appsettings ordering is the binder's; this type only adds the
/// conventional layer on top by reading the conventional var directly from <see cref="IConfiguration"/>
/// (which already merges environment variables, case-insensitively) and applying it when present.
/// </para>
/// <para>
/// Inputs are <see cref="IConfiguration"/> and process environment only — no DB, no network, no persisted
/// state (NFR-04). A resolved secret <b>value</b> is never logged or surfaced; only the variable name +
/// provider + field are logged, at <c>Debug</c> (NFR-03).
/// </para>
/// </summary>
internal sealed class ImposterOptionsPostConfigure(
    IConfiguration configuration,
    ILogger<ImposterOptionsPostConfigure> logger) : IPostConfigureOptions<ImposterOptions>
{
    private static readonly string[] SharedProviderSecretSuffixes = ["-anthropic", "-openai"];

    /// <summary>
    /// The conventional suffix → field surface. <see cref="ConventionalField.PropertyName"/> names the
    /// <see cref="ProviderOptions"/> property each suffix targets; a guard test asserts this set covers
    /// every conventionally-overridable scalar, so a new scalar field can't silently lack a suffix
    /// (LADR-02 Open item). <c>Models[]</c> and the identity field <c>Name</c> are intentionally absent.
    /// </summary>
    internal static readonly IReadOnlyList<ConventionalField> Fields =
    [
        new("_API_KEY", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        // Auth-typed alias for the same Secret slot: a Bearer subscription token (e.g. from
        // `claude setup-token` on a personal provider) reads more naturally as
        // <NAME>_AUTHORIZATION_BEARER than as <NAME>_API_KEY. Both suffixes target Secret; _API_KEY is
        // canonical and wins if both are set for one provider (first-present-wins guard in PostConfigure).
        new("_AUTHORIZATION_BEARER", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        new("_BASE_URL", nameof(ProviderOptions.BaseUrl), static (p, v) => p.BaseUrl = v),
        new("_AUTH_SCHEME", nameof(ProviderOptions.AuthScheme), static (p, v) => p.AuthScheme = v),
        new("_DIALECT", nameof(ProviderOptions.Dialect), static (p, v) => p.Dialect = v),
        // IsDefault is a bool, so its Apply is a no-op: PostConfigure handles _IS_DEFAULT inline
        // because applying it requires bool.TryParse plus a Warning on an unparseable value, which
        // the Action<ProviderOptions, string> delegate (no logger) cannot do.
        new("_IS_DEFAULT", nameof(ProviderOptions.IsDefault), static (_, _) => { }),
        new("_OPENAI_UPSTREAM_API", nameof(ProviderOptions.OpenAiUpstreamApi), static (p, v) => p.OpenAiUpstreamApi = v),
        new("_REQUEST_NORMALIZATION", nameof(ProviderOptions.RequestNormalization), static (p, v) => p.RequestNormalization = v),
        new("_ANTHROPIC_VERSION", nameof(ProviderOptions.AnthropicVersion), static (p, v) => p.AnthropicVersion = v),
    ];

    public void PostConfigure(string? name, ImposterOptions options)
    {
        foreach ((string key, ProviderOptions provider) in options.Providers)
        {
            string prefix = ToEnvPrefix(key);
            if (prefix.Length == 0)
            {
                continue;
            }

            // Secret is reachable via two suffixes (_API_KEY canonical, _AUTHORIZATION_BEARER alias).
            // Apply the first present in Fields order and ignore the rest, so the canonical var always
            // wins over the alias when an operator (mis)configures both for one provider.
            bool secretApplied = false;

            foreach (ConventionalField field in Fields)
            {
                (string variable, string? value) = ResolveConventionalValue(key, options, field, prefix);
                if (value is null)
                {
                    continue;
                }

                if (field.PropertyName == nameof(ProviderOptions.Secret))
                {
                    if (secretApplied)
                    {
                        continue;
                    }

                    secretApplied = true;
                }

                if (field.PropertyName == nameof(ProviderOptions.IsDefault))
                {
                    if (!bool.TryParse(value, out bool isDefault))
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            logger.LogWarning(
                                "Ignoring conventional override {EnvVar} for provider {Provider} field {Field}: value '{Value}' is not a recognized boolean.",
                                variable, key, field.PropertyName, value);
                        }

                        continue;
                    }

                    provider.IsDefault = isDefault;
                    LogApplied(variable, key, field.PropertyName);
                    continue;
                }

                field.Apply(provider, value);
                LogApplied(variable, key, field.PropertyName);
            }
        }
    }

    private void LogApplied(string variable, string key, string propertyName)
    {
        // NFR-03: log the variable name + provider + field, never the resolved value.
        logger.LogDebug(
            "Applied conventional override {EnvVar} to provider {Provider} field {Field}.",
            variable, key, propertyName);
    }

    private (string Variable, string? Value) ResolveConventionalValue(
        string key,
        ImposterOptions options,
        ConventionalField field,
        string prefix)
    {
        string variable = prefix + field.Suffix;

        // IConfiguration already includes environment variables and matches keys
        // case-insensitively, so OPENCODE_GO_API_KEY (any casing) resolves here.
        string? value = configuration[variable];
        if (value is not null || field.PropertyName != nameof(ProviderOptions.Secret))
        {
            return (variable, value);
        }

        string? sharedPrefix = TrySharedProviderSecretPrefix(key, options);
        if (sharedPrefix is null)
        {
            return (variable, null);
        }

        string sharedVariable = sharedPrefix + field.Suffix;
        return (sharedVariable, configuration[sharedVariable]);
    }

    private static string? TrySharedProviderSecretPrefix(string key, ImposterOptions options)
    {
        foreach (string suffix in SharedProviderSecretSuffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string baseKey = key[..^suffix.Length];
            if (options.Providers.ContainsKey(baseKey) ||
                options.Providers.Keys.Any(providerKey =>
                    !string.Equals(providerKey, key, StringComparison.OrdinalIgnoreCase) &&
                    providerKey.StartsWith(baseKey + "-", StringComparison.OrdinalIgnoreCase)))
            {
                return ToEnvPrefix(baseKey);
            }
        }

        return null;
    }

    /// <summary>
    /// Normalizes a provider key to its conventional env prefix: uppercase, with every run of
    /// non-alphanumeric characters collapsed to a single underscore (<c>opencode-go</c> →
    /// <c>OPENCODE_GO</c>, <c>opencode.anthropic</c> → <c>OPENCODE_ANTHROPIC</c>). The trailing
    /// suffix (e.g. <c>_API_KEY</c>) carries its own leading underscore.
    /// </summary>
    internal static string ToEnvPrefix(string key)
    {
        var builder = new System.Text.StringBuilder(key.Length);
        bool lastWasUnderscore = false;

        foreach (char c in key)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
                lastWasUnderscore = false;
            }
            else if (!lastWasUnderscore)
            {
                builder.Append('_');
                lastWasUnderscore = true;
            }
        }

        // A leading/trailing run of separators would leave a stray underscore; trim it so
        // "-opencode-" → "OPENCODE", keeping the prefix clean before the suffix is appended.
        return builder.ToString().Trim('_');
    }

    /// <summary>One conventional suffix, the provider field it targets, and how to apply a string value.</summary>
    internal sealed record ConventionalField(string Suffix, string PropertyName, Action<ProviderOptions, string> Apply);
}
