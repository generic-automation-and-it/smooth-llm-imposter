using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Routing;

/// <summary>
/// L0 — the Copilot client metadata mirrors opencode: interaction type from the agent's own request-class hints
/// (Claude Code, Codex), and initiator/vision from the forwarded body, since neither agent sends a header for them.
/// </summary>
public class GithubCopilotRequestHintsTests
{
    [Theory]
    [InlineData("x-claude-code-request-class", "main", "conversation-agent")]
    [InlineData("x-claude-code-request-class", "subagent", "conversation-subagent")]
    [InlineData("x-claude-code-request-class", "workflow", "conversation-subagent")]
    [InlineData("x-claude-code-request-class", "compaction", "conversation-compaction")]
    [InlineData("x-claude-code-request-class", "auxiliary", "conversation-background")]
    [InlineData("x-claude-code-request-class", "side_reply", "conversation-background")]
    [InlineData("x-claude-code-agent-id", "agent-123", "conversation-subagent")]
    [InlineData("x-openai-subagent", "compact", "conversation-compaction")]
    [InlineData("x-openai-subagent", "memory_consolidation", "conversation-background")]
    [InlineData("x-openai-subagent", "review", "conversation-subagent")]
    [InlineData("x-openai-subagent", "collab_spawn", "conversation-subagent")]
    [InlineData("x-app", "cli", "conversation-agent")]
    public void Interaction_type_follows_the_agent_request_class(string header, string value, string expected) =>
        GithubCopilotRequestHints.ResolveInteractionType(Headers((header, value))).ShouldBe(expected);

    [Fact]
    public void A_non_main_interaction_is_agent_initiated_even_on_a_user_turn()
    {
        GithubCopilotRequestHints hints = GithubCopilotRequestHints.Resolve(
            ApiDialect.Anthropic,
            Headers(("x-claude-code-request-class", "compaction")),
            """{"messages":[{"role":"user","content":"summarize"}]}""");

        hints.AgentInitiated.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}]}""", false)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]}]}""", false)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}]}""", true)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t"},{"type":"text","text":"also"}]}]}""", false)]
    [InlineData("""{"messages":[{"role":"assistant","content":"prefill"}]}""", true)]
    [InlineData("""{"messages":[]}""", true)]
    public void Anthropic_initiator_is_agent_for_tool_loop_continuations(string body, bool agent) =>
        GithubCopilotRequestHints.InspectBody(ApiDialect.Anthropic, body).Agent.ShouldBe(agent);

    [Theory]
    [InlineData("""{"input":"hi"}""", false)]
    [InlineData("""{"input":[{"role":"user","content":[{"type":"input_text","text":"hi"}]}]}""", false)]
    [InlineData("""{"input":[{"role":"user","content":"hi"},{"type":"function_call_output","call_id":"c","output":"ok"}]}""", true)]
    [InlineData("""{"input":[{"role":"assistant","content":"x"}]}""", true)]
    public void Responses_initiator_is_agent_unless_the_last_item_is_a_user_message(string body, bool agent) =>
        GithubCopilotRequestHints.InspectBody(ApiDialect.OpenAi, body).Agent.ShouldBe(agent);

    [Theory]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="}}]},{"role":"assistant","content":"ok"},{"role":"user","content":"next"}]}""", true)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AA=="}}]}]}]}""", true)]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"image"}]}]}""", false)]
    public void Anthropic_vision_detects_images_anywhere_in_the_conversation(string body, bool vision) =>
        GithubCopilotRequestHints.InspectBody(ApiDialect.Anthropic, body).Vision.ShouldBe(vision);

    [Theory]
    [InlineData("""{"input":[{"role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,AA=="}]}]}""", true)]
    [InlineData("""{"input":[{"type":"function_call_output","call_id":"c","output":[{"type":"input_image","image_url":"data:image/png;base64,AA=="}]}]}""", true)]
    [InlineData("""{"input":[{"role":"user","content":[{"type":"input_text","text":"input_image"}]}]}""", false)]
    public void Responses_vision_detects_input_images_in_messages_and_tool_output(string body, bool vision) =>
        GithubCopilotRequestHints.InspectBody(ApiDialect.OpenAi, body).Vision.ShouldBe(vision);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public void An_unreadable_body_is_a_user_turn_without_vision(string? body) =>
        GithubCopilotRequestHints.InspectBody(ApiDialect.Anthropic, body).ShouldBe((false, false));

    [Theory]
    [InlineData("X-Claude-Code-Session-Id", "cc-1")]
    [InlineData("session-id", "codex-1")]
    [InlineData("session_id", "codex-legacy-1")]
    public void Interaction_id_is_the_agent_session_id(string header, string value) =>
        GithubCopilotRequestHints.Resolve(ApiDialect.OpenAi, Headers((header, value)), body: null)
            .InteractionId.ShouldBe(value);

    [Fact]
    public void Interaction_id_is_omitted_without_a_session_header() =>
        GithubCopilotRequestHints.Resolve(ApiDialect.OpenAi, CallerHeaders.None, body: null)
            .InteractionId.ShouldBeNull();

    private static CallerHeaders Headers(params (string Name, string Value)[] headers) =>
        new(headers.Select(static h => new KeyValuePair<string, IReadOnlyList<string>>(h.Name, [h.Value])).ToList());
}
