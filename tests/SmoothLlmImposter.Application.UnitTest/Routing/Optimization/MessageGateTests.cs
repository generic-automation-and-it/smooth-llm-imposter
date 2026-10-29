using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class MessageGateTests
{
    [Fact]
    public async Task Positive_answer_at_the_threshold_reaches_the_optimizer()
    {
        JsonObject? request = null;
        var gate = Create(probability: 0.5, captured => request = captured);

        bool optimize = await gate.CanOptimizeAsync("Please kindly review this.", TestContext.Current.CancellationToken);

        optimize.ShouldBeTrue();
        request!["state"]!.GetValue<string>().ShouldBe("Please kindly review this.");
        JsonObject question = request["questions"]!["optimize"]!.AsObject();
        question["type"]!.GetValue<string>().ShouldBe("choice");
        question["criteria"]!["optimize"].ShouldNotBeNull();
        request.ContainsKey("model").ShouldBeFalse();
    }

    [Fact]
    public async Task Calls_and_log_fields_use_the_message_section_profile_not_the_tool_section()
    {
        // HLD 015 LADR-05: the gate resolves its own section; a tool-only setting never reaches it.
        using ServiceProvider services = BuildServices(new Dictionary<string, string?>
        {
            ["LLM_MESSAGES_ENABLED"] = "true",
            ["LLM_MESSAGES_BASE_URL"] = "http://optimizer.example",
            ["LLM_MESSAGES_MODEL"] = "optimizer",
            ["DECISIONS_TOOLS_BASE_URL"] = "https://tools.example",
            ["DECISIONS_TOOLS_MODEL"] = "tool-model",
            ["DECISIONS_MESSAGES_BASE_URL"] = "https://user:secret@messages.example/root?key=hidden",
            ["DECISIONS_MESSAGES_PATH"] = "/v1/gate",
            ["DECISIONS_MESSAGES_MODEL"] = "gate-model"
        });
        var client = (StubClient)services.GetRequiredService<IDecisionModelClient>();
        IMessageGate gate = services.GetRequiredService<IMessageGate>();

        await gate.CanOptimizeAsync("text", TestContext.Current.CancellationToken);

        client.Sections.ShouldBe(["Imposter:DecisionsMessages"]);
        gate.Model.ShouldBe("gate-model");
        gate.Endpoint.ShouldBe("https://messages.example/root/v1/gate");
        DecisionServiceOptions profile = services.GetRequiredService<DecisionServiceProfiles>()
            .Resolve("Imposter:DecisionsMessages");
        profile.Model.ShouldBe("gate-model");
        profile.Endpoint.ShouldBe("https://user:secret@messages.example/root?key=hidden/v1/gate");
    }

    [Theory]
    [InlineData(0.49, false)]
    [InlineData(0.5, true)]
    [InlineData(0.9, true)]
    public async Task Probability_is_compared_inclusively(double probability, bool expected)
    {
        MessageGate gate = Create(probability);

        (await gate.CanOptimizeAsync("text", TestContext.Current.CancellationToken)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"answers":{}}""")]
    [InlineData("""{"answers":{"optimize":{"probabilities":{"optimize":"high"}}}}""")]
    [InlineData("""{"answers":{"optimize":{"probabilities":{"optimize":1.5}}}}""")]
    public async Task A_missing_or_invalid_answer_throws(string envelope)
    {
        var client = new StubClient((_, _) => JsonNode.Parse(envelope)!.AsObject());
        MessageGate gate = Build(client);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            gate.CanOptimizeAsync("text", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        MessageGate gate = Build(new CancellingClient(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            gate.CanOptimizeAsync("text", cts.Token));
    }

    private static MessageGate Create(double probability, Action<JsonObject>? capture = null) =>
        Build(new StubClient((request, _) =>
        {
            capture?.Invoke(request);
            return Answers(probability);
        }));

    private static MessageGate Build(IDecisionModelClient client)
    {
        IOptions<ImposterOptions> imposter = Options.Create(new ImposterOptions
        {
            DecisionsMessages = new DecisionsMessagesOptions { Enabled = true, MinProbability = 0.5, Model = "gate-model" }
        });
        var factory = new OptionsFactory<DecisionServiceOptions>([new DecisionServiceOptionsSetup(imposter)], []);
        var profiles = new DecisionServiceProfiles(
            [new MessagesConsumer(imposter)],
            new OptionsMonitor<DecisionServiceOptions>(factory, [], new OptionsCache<DecisionServiceOptions>()));
        return new MessageGate(client, imposter, profiles);
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> values)
    {
        values.TryAdd("Imposter:Providers:default:Dialect", "openai");
        values.TryAdd("Imposter:Providers:default:BaseUrl", "https://api.example");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.Configure<ImposterOptions>(configuration.GetSection(ImposterOptions.SectionName));
        services.AddApplication();
        services.AddSingleton<IDecisionModelClient>(new StubClient((_, _) => Answers(0.9)));
        return services.BuildServiceProvider();
    }

    private static JsonObject Answers(double probability) => new()
    {
        ["answers"] = new JsonObject
        {
            ["optimize"] = new JsonObject
            {
                ["probabilities"] = new JsonObject { ["optimize"] = probability }
            }
        }
    };

    private sealed class StubClient(Func<JsonObject, CancellationToken, JsonObject> decide) : IDecisionModelClient
    {
        public List<string> Sections { get; } = [];

        public Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken)
        {
            Sections.Add(section);
            return Task.FromResult(decide(request, cancellationToken));
        }
    }

    private sealed class CancellingClient(CancellationToken token) : IDecisionModelClient
    {
        public Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken) =>
            Task.FromCanceled<JsonObject>(token);
    }
}
