extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// The mirror image of <see cref="StreamingDisconnectTests"/>: there the caller goes away, here the relay breaks
/// while the caller is still connected — the production symptom being an upstream that ends a chunked SSE body
/// without its terminating chunk (<c>HttpIOException/ResponseEnded</c>) after ~2.5 minutes of silence. That used
/// to escape into Kestrel's error handler after the response had started, which can only log a stack trace and cut
/// the socket, leaving the client with an unexplained truncation. The relay must now close the stream itself with
/// a terminal error frame in whichever SSE shape is already on the wire.
/// </summary>
public sealed class StreamFailureTests(StreamFailureAppFixture fixture) : IClassFixture<StreamFailureAppFixture>
{
    private const string PrematureEnd = "The response ended prematurely.";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Anthropic_stream_that_dies_mid_body_ends_with_a_named_error_event()
    {
        StaticLogCapture.Sink.Clear();
        ArrangeTruncatedSseUpstream("event: message_start\ndata: {\"type\":\"message_start\"}\n\n");

        string body = await PostAsync("/v1/messages", """{"model":"claude-sonnet-4","stream":true}""");

        // The partial answer the caller already received is preserved — the frame is appended, not substituted.
        body.ShouldContain("message_start");

        body.ShouldContain("event: error");
        JsonNode data = LastFrameData(body);
        data["type"]!.GetValue<string>().ShouldBe("error");
        data["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
        // Diagnosable: names the provider and carries the transport's own reason.
        data["error"]!["message"]!.GetValue<string>().ShouldContain("anthropic-official");
        data["error"]!["message"]!.GetValue<string>().ShouldContain(PrematureEnd);
    }

    [Fact]
    public async Task OpenAi_chat_stream_that_dies_mid_body_ends_with_an_unnamed_data_frame()
    {
        StaticLogCapture.Sink.Clear();
        ArrangeTruncatedSseUpstream("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n");

        string body = await PostAsync("/v1/chat/completions", """{"model":"gpt-4o","stream":true}""");

        // Chat Completions streams are unnamed frames; an `event:` line would be ignored by the OpenAI SDKs.
        body.ShouldNotContain("event: error");
        JsonNode data = LastFrameData(body);
        data["error"]!["type"]!.GetValue<string>().ShouldBe("upstream_error");
        data["error"]!["message"]!.GetValue<string>().ShouldContain(PrematureEnd);

        // No [DONE]: that sentinel means "completed normally", so a client ignoring the error frame must still see
        // an abnormal end rather than a silently truncated answer.
        body.ShouldNotContain("[DONE]");
    }

    [Fact]
    public async Task OpenAi_responses_stream_that_dies_mid_body_ends_with_a_responses_shaped_error_event()
    {
        StaticLogCapture.Sink.Clear();
        ArrangeTruncatedSseUpstream("event: response.created\ndata: {\"type\":\"response.created\"}\n\n");

        string body = await PostAsync("/openai/v1/responses", """{"model":"gpt-4o","stream":true}""");

        body.ShouldContain("event: error");
        JsonNode data = LastFrameData(body);
        // Responses events carry the discriminator in `data` itself rather than nesting under `error`.
        data["type"]!.GetValue<string>().ShouldBe("error");
        data["code"]!.GetValue<string>().ShouldBe("upstream_error");
        data["message"]!.GetValue<string>().ShouldContain(PrematureEnd);
    }

    [Fact]
    public async Task Stream_that_dies_before_the_first_byte_still_returns_a_502_json_error()
    {
        StaticLogCapture.Sink.Clear();
        ArrangeTruncatedSseUpstream(firstChunk: null);

        HttpResponseMessage response = await SendAsync("/v1/messages", """{"model":"claude-sonnet-4","stream":true}""");

        // Nothing had reached the wire, so a real status code and a dialect-shaped JSON body are still writable —
        // strictly more useful to a client than an SSE frame on a 200.
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        body["type"]!.GetValue<string>().ShouldBe("error");
        body["error"]!["message"]!.GetValue<string>().ShouldContain(PrematureEnd);
    }

    [Fact]
    public async Task Relay_failure_is_logged_once_as_a_handled_error_not_an_unhandled_request_fault()
    {
        StaticLogCapture.Sink.Clear();
        ArrangeTruncatedSseUpstream("event: message_start\ndata: {\"type\":\"message_start\"}\n\n");

        await PostAsync("/v1/messages", """{"model":"claude-sonnet-4","stream":true}""");

        // The cause is still logged with its exception — this is a real failure, not something to swallow.
        LogEvent[] errors = await WaitForAsync(events =>
            events.Where(e => e.Level >= LogEventLevel.Error && e.Exception is IOException).ToArray());
        errors.Length.ShouldBe(1);
        errors[0].RenderMessage().ShouldContain("anthropic-official");

        // But it no longer escapes request execution: the request-logging completion event carries no exception,
        // which is what produced the duplicate "unhandled exception" stack trace before the fix.
        LogEvent completion = await WaitForAsync(events => events.LastOrDefault(IsRequestCompletion));
        completion.Exception.ShouldBeNull();
    }

    private void ArrangeTruncatedSseUpstream(string? firstChunk) =>
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TruncatedStream(firstChunk))
            {
                Headers = { ContentType = new("text/event-stream") }
            }
        };

    private async Task<string> PostAsync(string path, string body) =>
        await (await SendAsync(path, body)).Content.ReadAsStringAsync(Ct);

    private async Task<HttpResponseMessage> SendAsync(string path, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        return await fixture.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    // The terminal frame is the last `data:` line in the stream.
    private static JsonNode LastFrameData(string body) =>
        JsonNode.Parse(body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])!;

    // Serilog's sinks are written after the response completes, so poll rather than assert immediately.
    private async Task<T> WaitForAsync<T>(Func<IReadOnlyList<LogEvent>, T?> select)
        where T : class
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (select(StaticLogCapture.Sink.Events) is { } match && match is not Array { Length: 0 })
            {
                return match;
            }

            await Task.Delay(100, Ct);
        }

        throw new InvalidOperationException("Expected log event was never captured.");
    }

    private static bool IsRequestCompletion(LogEvent e) =>
        e.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
        source.ToString().Contains("RequestLoggingMiddleware", StringComparison.Ordinal);

    /// <summary>
    /// Upstream body that optionally emits one SSE frame and then fails exactly as a culled chunked response does:
    /// <see cref="HttpIOException"/> with <see cref="HttpRequestError.ResponseEnded"/>, the same type the real
    /// <c>ChunkedEncodingReadStream</c> throws when the terminating chunk never arrives.
    /// </summary>
    private sealed class TruncatedStream(string? firstChunk) : Stream
    {
        private bool _served;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (firstChunk is not null && !_served)
            {
                _served = true;
                byte[] bytes = Encoding.UTF8.GetBytes(firstChunk);
                bytes.CopyTo(buffer.Span);
                return ValueTask.FromResult(bytes.Length);
            }

            throw new HttpIOException(HttpRequestError.ResponseEnded, PrematureEnd);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class StreamFailureAppFixture : WebApplicationFactory<HostApp::Program>
{
    public StubUpstreamHandler Upstream { get; } = new();

    // Keyless-free passthrough defaults only: the relay failure under test is dialect/path-shaped, not route-shaped,
    // so no imposter mapping is needed to reach the streaming path.
    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Imposter:Providers:openai-official:Dialect"] = "openai",
        ["Imposter:Providers:openai-official:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:openai-official:Secret"] = "openai-key",
        ["Imposter:Providers:openai-official:IsDefault"] = "true",

        ["Imposter:Providers:anthropic-official:Dialect"] = "anthropic",
        ["Imposter:Providers:anthropic-official:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:anthropic-official:Secret"] = "anthropic-key",
        ["Imposter:Providers:anthropic-official:IsDefault"] = "true",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(Config);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICredentialStore>();
            services.AddSingleton<ICredentialStore, NoopCredentialStore>();
            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    // Same wiring rationale as StreamingDisconnectAppFixture: the request-logging middleware writes through the
    // process-global Log.Logger, which Program sets during the build, so the redirect must run after it.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host = base.CreateHost(builder);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(StaticLogCapture.Sink)
            .CreateLogger();
        return host;
    }

    private sealed class NoopCredentialStore : ICredentialStore
    {
        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>(Array.Empty<ProviderCredential>());
        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential>(new OpenAiCredential("unused-provider", "unused", "cipher", CredentialAuthScheme.Bearer, null));
    }
}
