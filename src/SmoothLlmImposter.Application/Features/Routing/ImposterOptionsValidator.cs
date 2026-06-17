using Microsoft.Extensions.Options;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Fail-fast validation of <see cref="ImposterOptions"/> at startup (wired via <c>ValidateOnStart</c>):
/// every provider needs a unique name, a known dialect, an absolute base URL, and well-formed mappings;
/// each dialect may declare at most one default provider.
/// </summary>
internal sealed class ImposterOptionsValidator : IValidateOptions<ImposterOptions>
{
    public ValidateOptionsResult Validate(string? name, ImposterOptions options)
    {
        List<string> failures = [];

        if (options.Providers.Count == 0)
        {
            failures.Add("Imposter:Providers must contain at least one provider.");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaultsByDialect = new Dictionary<ApiDialect, int>();

        for (int i = 0; i < options.Providers.Count; i++)
        {
            ProviderOptions provider = options.Providers[i];
            string prefix = $"Imposter:Providers[{i}]";

            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                failures.Add($"{prefix}:Name is required.");
            }
            else if (!seenNames.Add(provider.Name))
            {
                failures.Add($"{prefix}:Name '{provider.Name}' is duplicated.");
            }

            if (!ApiDialectParser.TryParse(provider.Dialect, out ApiDialect dialect))
            {
                failures.Add($"{prefix}:Dialect '{provider.Dialect}' is invalid (expected 'openai' or 'anthropic').");
            }
            else if (provider.IsDefault)
            {
                defaultsByDialect[dialect] = defaultsByDialect.GetValueOrDefault(dialect) + 1;
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl) ||
                !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out _))
            {
                failures.Add($"{prefix}:BaseUrl '{provider.BaseUrl}' is not an absolute URL.");
            }

            for (int j = 0; j < provider.Models.Count; j++)
            {
                ModelMappingOptions mapping = provider.Models[j];
                if (string.IsNullOrWhiteSpace(mapping.From))
                {
                    failures.Add($"{prefix}:Models[{j}]:From is required.");
                }

                if (string.IsNullOrWhiteSpace(mapping.To))
                {
                    failures.Add($"{prefix}:Models[{j}]:To is required.");
                }
            }
        }

        foreach ((ApiDialect dialect, int count) in defaultsByDialect)
        {
            if (count > 1)
            {
                failures.Add($"Dialect '{dialect}' has {count} default providers; at most one is allowed.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
