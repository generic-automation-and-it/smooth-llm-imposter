using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class ErrorResponseFactoryTests
{
    private readonly ErrorResponseFactory _factory = new();

    [Fact]
    public void OpenAi_shape_nests_message_and_type_under_error()
    {
        JsonNode node = JsonNode.Parse(_factory.Create(ApiDialect.OpenAi, "boom", "upstream_error"))!;

        node["error"]!["message"]!.GetValue<string>().ShouldBe("boom");
        node["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
    }

    [Fact]
    public void Anthropic_shape_uses_top_level_error_type_marker()
    {
        JsonNode node = JsonNode.Parse(_factory.Create(ApiDialect.Anthropic, "boom", "upstream_error"))!;

        node["type"]!.GetValue<string>().ShouldBe("error");
        node["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
        node["error"]!["message"]!.GetValue<string>().ShouldBe("boom");
    }
}
