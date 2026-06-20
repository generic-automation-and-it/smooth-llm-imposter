using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Layers the conventional per-provider environment surface (HLD 007, LADR-02/03) onto the bound
/// <see cref="ImposterOptions"/>. For a provider keyed <c>opencode-go</c> the env prefix is
/// <c>OPENCODE_GO_</c>, and conventional suffixes map onto the provider's scalar fields
/// (<c>OPENCODE_GO_API_KEY</c> → <see cref="ProviderOptions.Secret"/>, etc.). Dialect-suffixed sibling
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
    /// <summary>
    /// The conventional suffix → field surface. <see cref="ConventionalField.PropertyName"/> names the
    /// <see cref="ProviderOptions"/> property each suffix targets; a guard test asserts this set covers
    /// every conventionally-overridable scalar, so a new scalar field can't silently lack a suffix
    /// (LADR-02 Open item). <c>Models[]</c> and the identity field <c>Name</c> are intentionally absent.
    /// </summary>
    internal static readonly IReadOnlyList<ConventionalField> Fields =
    [
        new("_API_KEY", nameof(ProviderOptions.Secret), static (p, v) => p.Secret = v),
        new("_BASE_URL", nameof(ProviderOptions.BaseUrl), static (p, v) => p.BaseUrl = v),
        new("_AUTH_SCHEME", nameof(ProviderOptions.AuthScheme), static (p, v) => p.AuthScheme = v),
        new("_DIALECT", nameof(ProviderOptions.Dialect), static (p, v) => p.Dialect = v),
        new("_IS_DEFAULT", nameof(ProviderOptions.IsDefault), static (p, v) =>
        {
            // Apply only on a parseable bool; an unparseable value leaves the bound value for the
            // validator to deal with (it never silently flips IsDefault).
            if (bool.TryParse(v, out bool isDefault))
            {
                p.IsDefault = isDefault;
            }
        }),
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

            foreach (ConventionalField field in Fields)
            {
                (string variable, string? value) = ResolveConventionalValue(key, options, field, prefix);
                if (value is null)
                {
                    continue;
                }

                field.Apply(provider, value);

                // NFR-03: log the variable name + provider + field, never the resolved value.
                logger.LogDebug(
                    "Applied conventional override {EnvVar} to provider {Provider} field {Field}.",
                    variable, key, field.PropertyName);
            }
        }
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
        foreach (string suffix in new[] { "-anthropic", "-openai" })
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
