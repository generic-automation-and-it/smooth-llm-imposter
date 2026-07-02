using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.UnitTest.Routing;

public class ModelMappingTests
{
    [Fact]
    public void ResolveTarget_expands_model_token_to_the_inbound_model()
    {
        ModelMapping mapping = new(From: "claude-opus-*", To: "anthropic.{model}", Caching: false);

        mapping.ResolveTarget("claude-opus-4-1-20250805")
            .ShouldBe("anthropic.claude-opus-4-1-20250805");
    }

    [Fact]
    public void ResolveTarget_without_token_returns_To_unchanged()
    {
        ModelMapping mapping = new(From: "gpt-5.4-*", To: "gpt-5.4-2026-03-05", Caching: false);

        mapping.ResolveTarget("gpt-5.4-preview").ShouldBe("gpt-5.4-2026-03-05");
    }

    [Fact]
    public void ResolveTarget_expands_every_occurrence_of_the_token()
    {
        ModelMapping mapping = new(From: "m-*", To: "{model}/{model}", Caching: false);

        mapping.ResolveTarget("m-1").ShouldBe("m-1/m-1");
    }
}
