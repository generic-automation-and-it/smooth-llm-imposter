extern alias HostApp;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class DecisionToolsIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/openai/v1/chat/completions", "chat", false)]
    [InlineData("/openai/v1/chat/completions", "chat", true)]
    [InlineData("/v1/chat/completions", "chat", false)]
    [InlineData("/v1/chat/completions", "chat", true)]
    [InlineData("/openai/v1/responses", "responses", false)]
    [InlineData("/openai/v1/responses", "responses", true)]
    [InlineData("/v1/responses", "responses", false)]
    [InlineData("/v1/responses", "responses", true)]
    [InlineData("/anthropic/v1/messages", "anthropic", false)]
    [InlineData("/anthropic/v1/messages", "anthropic", true)]
    [InlineData("/v1/messages", "anthropic", false)]
    [InlineData("/v1/messages", "anthropic", true)]
    public async Task Enabled_filter_reaches_every_api_on_passthrough_and_imposter_routes(
        string path, string shape, bool imposter)
    {
        var decisions = new StubDecisionClient();
        using var app = new App(decisions, enabled: true);
        string model = imposter ? $"{shape}-imposter" : "unmatched-model";

        using HttpResponseMessage response = await app.Client.PostAsync(path, Json(Body(shape, model)), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Upstream.RequestCount.ShouldBe(1);
        decisions.Requests.Count.ShouldBe(1);
        JsonObject request = decisions.Requests.Single();
        request["state"]!.GetValue<string>().ShouldContain("Read the file");
        request["questions"]!.AsObject().Count.ShouldBe(2);
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["model"]!.GetValue<string>().ShouldBe(imposter ? "upstream-model" : model);
        JsonArray tools = forwarded["tools"]!.AsArray();
        tools.Count.ShouldBe(1);
        ToolName(tools[0]!, shape).ShouldBe("read_file");
        forwarded.ContainsKey("tool_choice").ShouldBeFalse();
        app.Upstream.LastRequestUri!.Host.ShouldBe(imposter ? "imposter.test" : "official.test");
        app.Upstream.LastRequestUri.AbsolutePath.ShouldBe(path.StartsWith("/openai", StringComparison.Ordinal)
            ? path["/openai".Length..]
            : path.StartsWith("/anthropic", StringComparison.Ordinal) ? path["/anthropic".Length..] : path);
    }

    [Fact]
    public async Task Claude_code_reminder_blocks_are_not_sent_as_decision_state()
    {
        var decisions = new StubDecisionClient();
        using var app = new App(decisions, enabled: true);
        string body = """
            { "model": "unmatched-model", "max_tokens": 100, "messages": [{"role":"user","content":[
                {"type":"text","text":"<system-reminder>\nCodebase and user instructions are shown below.\n</system-reminder>"},
                {"type":"text","text":"<system_instruction>\nYou are working inside Conductor.\n</system_instruction>\n\nRead the file"}]}],
              "tools": [{"name":"read_file","description":"Read a local file","input_schema":{"type":"object"}},
                        {"name":"web_search","description":"Search the web","input_schema":{"type":"object"}}] }
            """;

        using HttpResponseMessage response = await app.Client.PostAsync("/anthropic/v1/messages", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        decisions.Requests.Single()["state"]!.GetValue<string>().ShouldBe("User request:\nRead the file");
        app.Upstream.LastRequestBody!.ShouldContain(body[body.IndexOf("\"messages\"", StringComparison.Ordinal)..body.IndexOf(",\n  \"tools\"", StringComparison.Ordinal)]);
        JsonArray tools = JsonNode.Parse(app.Upstream.LastRequestBody!)!["tools"]!.AsArray();
        tools.ShouldHaveSingleItem();
        ToolName(tools[0]!, "anthropic").ShouldBe("read_file");
    }

    [Fact]
    public async Task Default_selection_keeps_the_core_tools_and_at_least_the_minimum_number_of_tools()
    {
        var decisions = new StubDecisionClient
        {
            Decide = static (request, _) =>
            {
                var answers = new JsonObject();
                foreach ((string key, JsonNode? _) in request["questions"]!.AsObject())
                {
                    answers[key] = new JsonObject { ["probabilities"] = new JsonObject { ["use"] = key == "t5" ? 0.9 : 0.1 } };
                }

                return Task.FromResult(new JsonObject { ["answers"] = answers });
            }
        };
        using var app = new App(decisions, enabled: true, defaultSelection: true);
        string[] names = ["Bash", "Read", "Edit", "Write", "Skill"];
        string tools = string.Join(",", names.Concat(Enumerable.Range(0, 35).Select(index => $"extra{index}"))
            .Select(name => $$$"""{"name":"{{{name}}}","description":"d","input_schema":{"type":"object"}}"""));
        string body = $$"""{"model":"unmatched-model","max_tokens":10,"messages":[{"role":"user","content":"Analyse it"}],"tools":[{{tools}}]}""";

        using HttpResponseMessage response = await app.Client.PostAsync("/anthropic/v1/messages", Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        decisions.Requests.Sum(request => request["questions"]!.AsObject().Count).ShouldBe(35);
        string[] forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!["tools"]!.AsArray()
            .Select(tool => tool!["name"]!.GetValue<string>()).ToArray();
        forwarded.Length.ShouldBe(20);
        forwarded.Take(5).ShouldBe(names);
        forwarded.ShouldContain("extra0");
    }

    [Theory]
    [InlineData("/openai/v1/responses")]
    [InlineData("/v1/responses")]
    public async Task Responses_downgrade_filters_the_transformed_chat_catalog(string path)
    {
        var decisions = new StubDecisionClient();
        using var app = new App(decisions, enabled: true);

        using HttpResponseMessage response = await app.Client.PostAsync(path,
            Json(Body("responses", "downgrade-imposter")), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Upstream.LastRequestUri!.AbsolutePath.ShouldBe("/v1/chat/completions");
        decisions.Requests.Count.ShouldBe(1);
        JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
        forwarded["messages"].ShouldBeOfType<JsonArray>();
        forwarded.ContainsKey("input").ShouldBeFalse();
        forwarded["model"]!.GetValue<string>().ShouldBe("upstream-model");
        JsonArray tools = forwarded["tools"]!.AsArray();
        tools.Count.ShouldBe(1);
        ToolName(tools[0]!, "chat").ShouldBe("read_file");
    }

    [Theory]
    [InlineData("/openai/v1/chat/completions", "chat")]
    [InlineData("/v1/chat/completions", "chat")]
    [InlineData("/openai/v1/responses", "responses")]
    [InlineData("/v1/responses", "responses")]
    [InlineData("/anthropic/v1/messages", "anthropic")]
    [InlineData("/v1/messages", "anthropic")]
    public async Task Default_disabled_filter_preserves_the_exact_inbound_body(string path, string shape)
    {
        var decisions = new StubDecisionClient();
        using var app = new App(decisions);
        string body = Body(shape, "unmatched-model");

        using HttpResponseMessage response = await app.Client.PostAsync(path, Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Upstream.LastRequestBody.ShouldBe(body);
        decisions.Requests.ShouldBeEmpty();
        app.Logs.Entries.ShouldContain(entry => entry.Category.Contains("DecisionToolsFilter", StringComparison.Ordinal));
        app.Logs.Entries.ShouldNotContain(entry =>
            entry.Category.Contains("DecisionToolsFilter", StringComparison.Ordinal) && entry.Level > LogLevel.Debug);
    }

    [Theory]
    [InlineData("/openai/v1/chat/completions", "chat")]
    [InlineData("/openai/v1/responses", "responses")]
    [InlineData("/anthropic/v1/messages", "anthropic")]
    public async Task Decision_failure_forwards_the_original_body(string path, string shape)
    {
        var decisions = new StubDecisionClient
        {
            Decide = (_, _) => throw new HttpRequestException("classifier unavailable")
        };
        using var app = new App(decisions, enabled: true);
        string body = Body(shape, "unmatched-model");

        using HttpResponseMessage response = await app.Client.PostAsync(path, Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        decisions.Requests.Count.ShouldBe(1);
        app.Upstream.LastRequestBody.ShouldBe(body);
        app.Logs.Entries.ShouldContain(entry =>
            entry.Category.Contains("DecisionToolsFilter", StringComparison.Ordinal) && entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("/openai/v1/chat/completions", "chat")]
    [InlineData("/anthropic/v1/messages", "anthropic")]
    public async Task Who_probe_with_tools_bypasses_the_decision_service(string path, string shape)
    {
        var decisions = new StubDecisionClient();
        using var app = new App(decisions, enabled: true);
        string body = Body(shape, "unmatched-model").Replace("Read the file", "--who?", StringComparison.Ordinal);

        using HttpResponseMessage response = await app.Client.PostAsync(path, Json(body), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string reply = await response.Content.ReadAsStringAsync(Ct);
        reply.ShouldContain("Passthrough:");
        decisions.Requests.ShouldBeEmpty();
        app.Upstream.RequestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("/openai/v1/chat/completions", "chat")]
    [InlineData("/anthropic/v1/messages", "anthropic")]
    public async Task Caller_abort_during_decisions_makes_no_upstream_call_and_logs_no_warning(
        string path, string shape)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisions = new StubDecisionClient
        {
            Decide = async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Expected caller cancellation.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    aborted.TrySetResult();
                    throw;
                }
            }
        };
        using var app = new App(decisions, enabled: true);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<HttpResponseMessage> pending = app.Client.PostAsync(path, Json(Body(shape, "unmatched-model")), caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        caller.Cancel();

        await aborted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await app.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        try
        {
            using HttpResponseMessage response = await pending;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
        }

        decisions.Requests.Count.ShouldBe(1);
        app.Upstream.RequestCount.ShouldBe(0);
        app.Logs.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Warning);
        app.Escaped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sticky_session_keeps_a_tool_sent_earlier_while_chats_without_session_ids_share_nothing()
    {
        bool firstTurn = true;
        var decisions = new StubDecisionClient
        {
            Decide = (request, _) =>
            {
                var answers = new JsonObject();
                foreach ((string key, JsonNode? question) in request["questions"]!.AsObject())
                {
                    string instructions = question!["instructions"]!.GetValue<string>();
                    double use = instructions.Contains("read_file", StringComparison.Ordinal) ||
                        (firstTurn && instructions.Contains("web_search", StringComparison.Ordinal)) ? 0.9 : 0.1;
                    answers[key] = new JsonObject { ["probabilities"] = new JsonObject { ["use"] = use } };
                }

                return Task.FromResult(new JsonObject { ["answers"] = answers });
            }
        };
        using var app = new App(decisions, enabled: true, sticky: true);
        string Turn(string input) => Body("responses", "caching-imposter").Replace("Read the file", input, StringComparison.Ordinal);
        async Task<string[]> Send(string input, string? session)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = Json(Turn(input)) };
            if (session is not null)
            {
                request.Headers.Add("x-session-id", session);
            }
            using HttpResponseMessage response = await app.Client.SendAsync(request, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonObject forwarded = JsonNode.Parse(app.Upstream.LastRequestBody!)!.AsObject();
            forwarded["prompt_cache_key"]!.GetValue<string>().ShouldBe("caching-imposter");
            return forwarded["tools"]!.AsArray().Select(tool => ToolName(tool!, "responses")).ToArray();
        }

        (await Send("Read the file", "chat")).ShouldBe(["read_file", "web_search"]);
        (await Send("Read the file", null)).ShouldBe(["read_file", "web_search"]);
        firstTurn = false;

        (await Send("Read it again", "chat")).ShouldBe(["read_file", "web_search"]);
        (await Send("Read it again", null)).ShouldBe(["read_file"]);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Body(string shape, string model) => shape switch
    {
        "chat" => $$$$"""
            { "model": "{{{{model}}}}", "messages": [{"role":"user","content":"Read the file"}],
              "tools": [{"type":"function","function":{"name":"read_file","description":"Read a local file","parameters":{"type":"object"}}},
                        {"type":"function","function":{"name":"web_search","description":"Search the web","parameters":{"type":"object"}}}],
              "tool_choice": {"type":"function","function":{"name":"web_search"}} }
            """,
        "responses" => $$$$"""
            { "model": "{{{{model}}}}", "input": "Read the file",
              "tools": [{"type":"function","name":"read_file","description":"Read a local file","parameters":{"type":"object"}},
                        {"type":"function","name":"web_search","description":"Search the web","parameters":{"type":"object"}}],
              "tool_choice": {"type":"function","name":"web_search"} }
            """,
        "anthropic" => $$$$"""
            { "model": "{{{{model}}}}", "max_tokens": 100, "messages": [{"role":"user","content":"Read the file"}],
              "tools": [{"name":"read_file","description":"Read a local file","input_schema":{"type":"object"}},
                        {"name":"web_search","description":"Search the web","input_schema":{"type":"object"}}],
              "tool_choice": {"type":"tool","name":"web_search"} }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static string ToolName(JsonNode tool, string shape) =>
        (shape == "chat" ? tool["function"]!["name"] : tool["name"])!.GetValue<string>();

    private sealed class StubDecisionClient : IDecisionModelClient
    {
        public ConcurrentQueue<JsonObject> Requests { get; } = new();

        public Func<JsonObject, CancellationToken, Task<JsonObject>> Decide { get; init; } =
            static (request, _) =>
            {
                var answers = new JsonObject();
                foreach ((string key, JsonNode? question) in request["questions"]!.AsObject())
                {
                    double use = question!["instructions"]!.GetValue<string>().Contains("read_file", StringComparison.Ordinal) ? 0.9 : 0.1;
                    answers[key] = new JsonObject { ["probabilities"] = new JsonObject { ["use"] = use, ["skip"] = 1 - use } };
                }

                return Task.FromResult(new JsonObject { ["answers"] = answers });
            };

        public Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.DeepClone().AsObject());
            return Decide(request, cancellationToken);
        }
    }

    private sealed class App : IDisposable
    {
        private readonly ImposterAppFixture _base = new();
        private readonly WebApplicationFactory<HostApp::Program> _factory;

        public App(IDecisionModelClient decisions, bool? enabled = null, bool defaultSelection = false, bool sticky = false)
        {
            _factory = _base.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.Sources.Clear();
                    config.AddInMemoryCollection(Config(enabled, defaultSelection, sticky));
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IDecisionModelClient>();
                    services.AddSingleton(decisions);
                    services.RemoveAll(typeof(ILogger<>));
                    services.AddSingleton(Logs);
                    services.AddSingleton(typeof(ILogger<>), typeof(CaptureLogger<>));
                    services.AddSingleton<IStartupFilter>(new CompletionFilter(Completed, Escaped));
                });
            });
            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }
        public StubUpstreamHandler Upstream => _base.Upstream;
        public LogCapture Logs { get; } = new();
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Exception> Escaped { get; } = new();

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            _base.Dispose();
        }

        private static Dictionary<string, string?> Config(bool? enabled, bool defaultSelection, bool sticky)
        {
            var config = new Dictionary<string, string?> { ["Imposter:DecisionsTools:StickyPerSession"] = sticky.ToString() };
            if (enabled.HasValue)
            {
                config["Imposter:DecisionsTools:Enabled"] = enabled.Value.ToString();
            }

            if (!defaultSelection)
            {
                config["Imposter:DecisionsTools:MinKeepTools"] = "0";
                config["Imposter:DecisionsTools:AlwaysKeep"] = "none-configured";
            }

            foreach (string dialect in new[] { "openai", "anthropic" })
            {
                string prefix = $"Imposter:Providers:{dialect}-official";
                config[$"{prefix}:Dialect"] = dialect;
                config[$"{prefix}:BaseUrl"] = "https://official.test";
                config[$"{prefix}:IsDefault"] = "true";
            }

            foreach (string shape in new[] { "chat", "responses", "anthropic", "downgrade", "caching" })
            {
                string prefix = $"Imposter:Providers:{shape}-mapped";
                config[$"{prefix}:Dialect"] = shape == "anthropic" ? "anthropic" : "openai";
                config[$"{prefix}:BaseUrl"] = "https://imposter.test";
                config[$"{prefix}:Models:0:From"] = $"{shape}-imposter";
                config[$"{prefix}:Models:0:To"] = "upstream-model";
                if (shape == "downgrade")
                {
                    config[$"{prefix}:OpenAiUpstreamApi"] = "chat_completions";
                }
                if (shape == "caching")
                {
                    config[$"{prefix}:Models:0:Caching"] = "true";
                }
            }

            return config;
        }
    }

    private sealed class CompletionFilter(TaskCompletionSource completed, ConcurrentQueue<Exception> escaped) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (_, nextMiddleware) =>
            {
                try
                {
                    await nextMiddleware();
                }
                catch (Exception failure)
                {
                    escaped.Enqueue(failure);
                    throw;
                }
                finally
                {
                    completed.TrySetResult();
                }
            });
            next(app);
        };
    }

    private sealed class LogCapture
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();
    }

    private sealed class CaptureLogger<T>(LogCapture capture) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            capture.Entries.Enqueue((typeof(T).FullName!, logLevel, formatter(state, exception)));
    }
}
