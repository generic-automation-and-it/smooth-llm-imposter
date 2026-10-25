using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Bridge;

public class AnthropicChatTokenEstimatorTests
{
    private static readonly IAnthropicChatBridge Bridge = new AnthropicChatBridge(
        new AnthropicChatRequestMapper(NullLogger<AnthropicChatRequestMapper>.Instance),
        new AnthropicChatResponseMapper(NullLogger<AnthropicChatResponseMapper>.Instance),
        new AnthropicChatStreamTranslator(NullLogger<AnthropicChatStreamTranslator>.Instance),
        TimeProvider.System,
        NullLogger<AnthropicChatBridge>.Instance);

    private static int Count(string body) =>
        AnthropicChatJson.TryParseObject(Bridge.CountTokens(body, new AnthropicChatBridgeOptions()))!["input_tokens"]!.GetValue<int>();

    private static int Estimate(string body, AnthropicChatBridgeOptions? options = null) =>
        AnthropicChatTokenEstimator.RequestEstimate(AnthropicChatJson.TryParseObject(body)!, options ?? new());

    [Fact]
    public void T01_hello_is_nine()
    {
        Count("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""").ShouldBe(9);
    }

    [Fact]
    public void T05_empty_messages_is_three()
    {
        Count("""{"messages":[]}""").ShouldBe(3);
    }

    [Fact]
    public void T03_T04_count_tokens_validation()
    {
        Should.Throw<AnthropicChatRequestException>(() => Count("""{}"""))
            .Message.ShouldBe("messages: Field required");
        Should.Throw<AnthropicChatRequestException>(() => Count("""{"messages":"x"}"""))
            .Message.ShouldBe("messages: Input should be a valid list");
        Should.Throw<AnthropicChatRequestException>(() => Count("[]"))
            .Message.ShouldBe("Request body must be a JSON object.");
    }

    [Fact]
    public void T07_T08_system_and_attribution()
    {
        Count("""{"model":"m","messages":[{"role":"user","content":"hello"}],"system":"You are Claude Code."}""").ShouldBe(14);
        Count("""{"model":"m","messages":[{"role":"user","content":"hello"}],"system":[{"type":"text","text":"x-anthropic-billing-header: v"},{"type":"text","text":"You are Claude Code."}]}""").ShouldBe(14);
    }

    [Fact]
    public void T09_tool_units()
    {
        const string tools = """[{"name":"Read","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}]""";
        Count($"{{\"model\":\"m\",\"messages\":[{{\"role\":\"user\",\"content\":\"hello\"}}],\"tools\":{tools}}}").ShouldBe(51);
    }

    [Fact]
    public void T10_cjk()
    {
        Count("""{"model":"m","messages":[{"role":"user","content":"你好世界"}]}""").ShouldBe(13);
    }

    [Fact]
    public void T11_image_units()
    {
        Count("""{"model":"m","messages":[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://x"}}]}]}""").ShouldBe(1507);
    }

    [Fact]
    public void T14_document_placeholder_units()
    {
        Count("""{"model":"m","messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"document"}]}]}""").ShouldBe(22);
    }

    [Fact]
    public void T15_typed_server_tool_contributes_zero()
    {
        Count("""{"messages":[{"role":"user","content":"hello"}],"tools":[{"type":"web_search_20250305","name":"web_search"}]}""")
            .ShouldBe(9);
    }

    [Fact]
    public void T17_additivity_in_units()
    {
        int before = AnthropicChatTokenEstimator.RequestUnits(
            AnthropicChatJson.TryParseObject("""{"model":"m","messages":[{"role":"user","content":"hello"}]}""")!,
            new AnthropicChatBridgeOptions());
        int after = AnthropicChatTokenEstimator.RequestUnits(
            AnthropicChatJson.TryParseObject("""{"model":"m","messages":[{"role":"user","content":"hello"},{"role":"user","content":"hi"}]}""")!,
            new AnthropicChatBridgeOptions());
        (after - before).ShouldBe(18);
    }

    [Fact]
    public void T19_estimate_text_vectors()
    {
        AnthropicChatTokenEstimator.EstimateText("").ShouldBe(0);
        AnthropicChatTokenEstimator.EstimateText("Hello, world.").ShouldBe(4);
        AnthropicChatTokenEstimator.EstimateText("😀").ShouldBe(2);
    }

    [Fact]
    public void Document_image_mode_counts_as_image()
    {
        var options = new AnthropicChatBridgeOptions(DocumentMode: AnthropicChatDocumentMode.Image);
        Estimate("""{"messages":[{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"A"}}]}]}""", options)
            .ShouldBe(1507);
    }
}
