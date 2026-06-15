using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ImposterRouterTests
{
    private static ImposterRouter Build()
    {
        var options = Options.Create(new ImposterOptions
        {
            Providers =
            [
                new ProviderOptions
                {
                    Name = "opencode", Api = "openai", BaseUrl = "https://opencode.example",
                    Models = [new ModelMappingOptions { From = "gpt5.4", To = "grok-code", Caching = true }]
                },
                new ProviderOptions { Name = "openai-official", Api = "openai", BaseUrl = "https://api.openai.com", IsDefault = true }
            ]
        });

        var resolver = new RouteResolver(new ProviderCatalog(options));
        IRequestTransformer[] transformers = [new OpenAiRequestTransformer(), new AnthropicRequestTransformer()];
        return new ImposterRouter(resolver, transformers, NullLogger<ImposterRouter>.Instance);
    }

    [Fact]
    public void Plan_resolves_and_transforms_an_imposter_route()
    {
        ImposterRouter router = Build();

        RoutePlan plan = router.Plan(ApiDialect.OpenAi, """{"model":"gpt5.4"}""");

        plan.InboundModel.ShouldBe("gpt5.4");
        plan.Decision.Provider.Name.ShouldBe("opencode");
        plan.TransformedBody.ShouldContain("grok-code");
        plan.TransformedBody.ShouldContain("prompt_cache_key");
    }

    [Fact]
    public void Plan_throws_when_model_missing()
    {
        ImposterRouter router = Build();
        Should.Throw<RoutingException>(() => router.Plan(ApiDialect.OpenAi, """{"messages":[]}"""));
    }

    [Fact]
    public void Plan_throws_on_non_object_body()
    {
        ImposterRouter router = Build();
        Should.Throw<RoutingException>(() => router.Plan(ApiDialect.OpenAi, "[]"));
    }
}
