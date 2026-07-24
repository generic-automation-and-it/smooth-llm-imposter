using System.Reflection;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ProviderOptionsClonerTests
{
    [Fact]
    public void Clone_copies_every_scalar_field()
    {
        // A new scalar ProviderOptions field must be added to Clone, or a CRUD update silently drops it.
        // Reflection over string/bool properties guards against that drift (AuthHeader was one such field).
        var source = new ProviderOptions
        {
            Name = "display",
            Dialect = "openai",
            BaseUrl = "https://o.example",
            Secret = "sk-secret",
            AuthScheme = "ApiKey",
            AuthHeader = "api-key",
            IsDefault = true,
            Enabled = false,
            AnthropicVersion = "2023-06-01",
            OpenAiUpstreamApi = "chat_completions",
            RequestNormalization = "codex_to_openai_sdk",
            SessionForwarding = "opencode-go"
        };

        ProviderOptions clone = ProviderOptionsCloner.Clone(source);

        foreach (PropertyInfo property in typeof(ProviderOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(bool)))
        {
            property.GetValue(clone).ShouldBe(property.GetValue(source), $"{property.Name} should be cloned");
        }
    }
}
