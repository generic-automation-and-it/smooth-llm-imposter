using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Normalization;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Upstream.EvalTest;

/// <summary>
/// L3 — live upstream conformance eval (HLD 004 LADR-04 / NFR-04). Replays the tool-validation matrix
/// against the real <c>opencode-go</c> upstream to prove (a) a raw Codex tool catalog run through our
/// <see cref="CodexToOpenAiSdkNormalizer"/> is accepted (200), and (b) the upstream contract still holds
/// (an unsupported tool type is still rejected with 400, so normalization is still required).
/// <para>
/// Secret-gated: every test is <b>neutral</b> (skipped) when <c>OPENCODE_API_KEY</c> is absent — this
/// tier never runs in the hermetic L0/L2 pr-gate (the project is excluded from the solution) and is
/// invoked only by the secret-gated <c>pr-evals-gate</c> workflow. The key is never logged.
/// </para>
/// </summary>
public sealed class OpencodeToolNormalizationEvalTests
{
    private const string ChatCompletionsUrl = "https://opencode.ai/zen/go/v1/chat/completions";
    private const string TargetModel = "kimi-k2.7-code";

    // A Codex-shaped catalog: a namespace wrapper (GitHub connector), an unsupported tool type, a dotted
    // name, and a valid function — i.e. the exact shapes that 400 the strict upstream before normalization.
    // It also carries a developer-role message: Moonshot rejects "developer" ("tokenization failed"), so the
    // Responses→Chat conversion must fold it to "system". Together these reproduce the full #19 failure.
    private const string RawCodexBody = """
    {"model":"gpt-5.4",
     "messages":[
       {"role":"developer","content":"Be concise."},
       {"role":"user","content":"Reply with the single word: ok"}
     ],
     "max_tokens":16,
     "tools":[
       {"type":"namespace","name":"mcp__codex_apps__github","tools":[
         {"type":"function","name":"_search_issues","parameters":{"type":"object","properties":{}}}
       ]},
       {"type":"web_search","external_web_access":true},
       {"type":"function","name":"multi_tool_use.parallel","parameters":{"type":"object","properties":{}}},
       {"type":"function","name":"exec_command","parameters":{"type":"object","properties":{}}}
     ]}
    """;

    [Fact]
    public async Task Normalized_codex_catalog_is_accepted_by_live_upstream()
    {
        string apiKey = RequireApiKey();

        var transformer = new OpenAiRequestTransformer([new CodexToOpenAiSdkNormalizer()]);
        var decision = new RouteDecision(
            ChatProvider(RequestNormalization.CodexToOpenAiSdk),
            TargetModel,
            CachingEnabled: false,
            IsImposter: true);

        string normalized = transformer.Transform(RawCodexBody, decision, "gpt-5.4");

        // Sanity-check our own output before sending it: only valid function tools survive.
        JsonObject normalizedRoot = JsonNode.Parse(normalized)!.AsObject();
        string[] names = [.. normalizedRoot["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];
        names.ShouldBe(["_search_issues", "exec_command"]);

        using HttpResponseMessage response = await PostAsync(normalized, apiKey);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"normalized request should be accepted; upstream returned {(int)response.StatusCode}: {await SafeBody(response)}");
    }

    [Fact]
    public async Task Unnormalized_codex_catalog_is_still_rejected_by_live_upstream()
    {
        string apiKey = RequireApiKey();

        // The raw catalog (model swapped to the real upstream model) must still 400 — proving the upstream
        // contract is unchanged and the proxy-side normalization is genuinely required.
        JsonObject raw = JsonNode.Parse(RawCodexBody)!.AsObject();
        raw["model"] = TargetModel;

        using HttpResponseMessage response = await PostAsync(raw.ToJsonString(), apiKey);

        response.StatusCode.ShouldBe(
            HttpStatusCode.BadRequest,
            $"unsupported tool types should still be rejected; upstream returned {(int)response.StatusCode}: {await SafeBody(response)}");
    }

    private static ProviderRoute ChatProvider(RequestNormalization normalization) =>
        new(
            "opencode-go",
            ApiDialect.OpenAi,
            new Uri("https://opencode.ai/zen/go"),
            Secret: null,
            IsDefault: false,
            AnthropicVersion: null,
            Models: [],
            OpenAiUpstreamApi: OpenAiUpstreamApi.ChatCompletions,
            AuthScheme: null,
            RequestNormalization: normalization,
            Enabled: true);

    private static async Task<HttpResponseMessage> PostAsync(string body, string apiKey)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> SafeBody(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    // Neutral when the org secret is absent (fork PRs / local default): the tier is skipped, not failed.
    private static string RequireApiKey()
    {
        string? key = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip("OPENCODE_API_KEY is not set — skipping the live upstream eval (L3 is neutral without the secret).");
        }

        return key!;
    }
}
