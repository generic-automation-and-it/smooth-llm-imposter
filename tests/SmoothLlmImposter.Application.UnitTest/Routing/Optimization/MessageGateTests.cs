using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
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

    private static MessageGate Build(IDecisionModelClient client) =>
        new(client,
            Options.Create(new ImposterOptions
            {
                DecisionsMessages = new DecisionsMessagesOptions { Enabled = true, MinProbability = 0.5 }
            }),
            Options.Create(new DecisionServiceOptions
            {
                BaseUrl = "http://localhost:1234",
                Path = "/v1/systemone",
                Model = "gate-model"
            }));

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
        public Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken) =>
            Task.FromResult(decide(request, cancellationToken));
    }

    private sealed class CancellingClient(CancellationToken token) : IDecisionModelClient
    {
        public Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken) =>
            Task.FromCanceled<JsonObject>(token);
    }
}
