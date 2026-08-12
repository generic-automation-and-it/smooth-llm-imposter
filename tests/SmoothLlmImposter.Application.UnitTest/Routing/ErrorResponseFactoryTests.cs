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

    [Fact]
    public void Anthropic_stream_frame_names_the_event_and_reuses_the_dialect_envelope()
    {
        string frame = _factory.CreateStreamErrorFrame(StreamErrorFraming.Anthropic, "boom", "upstream_error");

        frame.ShouldStartWith("event: error\ndata: ");
        frame.ShouldEndWith("\n\n");

        JsonNode data = FrameData(frame);
        data["type"]!.GetValue<string>().ShouldBe("error");
        data["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
        data["error"]!["message"]!.GetValue<string>().ShouldBe("boom");
    }

    [Fact]
    public void OpenAi_chat_stream_frame_is_unnamed_because_chat_streams_carry_no_event_lines()
    {
        string frame = _factory.CreateStreamErrorFrame(StreamErrorFraming.OpenAiChat, "boom", "upstream_error");

        frame.ShouldStartWith("data: ");
        frame.ShouldNotContain("event:");
        frame.ShouldEndWith("\n\n");

        JsonNode data = FrameData(frame);
        data["error"]!["message"]!.GetValue<string>().ShouldBe("boom");
        data["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
    }

    [Fact]
    public void OpenAi_responses_stream_frame_puts_the_discriminator_in_data_not_under_error()
    {
        string frame = _factory.CreateStreamErrorFrame(StreamErrorFraming.OpenAiResponses, "boom", "upstream_error");

        frame.ShouldStartWith("event: error\ndata: ");

        JsonNode data = FrameData(frame);
        data["type"]!.GetValue<string>().ShouldBe("error");
        data["code"]!.GetValue<string>().ShouldBe("upstream_error");
        data["message"]!.GetValue<string>().ShouldBe("boom");
        data["error"].ShouldBeNull();
    }

    private static JsonNode FrameData(string frame) =>
        JsonNode.Parse(frame
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])!;
}
