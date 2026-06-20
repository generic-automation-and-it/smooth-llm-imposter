extern alias HostApp;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Regression test for issue #17: a client that disconnects mid-stream must not surface as an unhandled
/// exception in the logs. Serilog's request-logging middleware logs the escaping exception at Error via the
/// process-global <see cref="Log.Logger"/> (the production symptom: a TaskCanceledException stack trace at
/// error level). The fixture redirects that static logger into an in-memory sink so the test can assert the
/// aborted request completes cleanly. Asserting on the static logger requires the suite to run serially —
/// see <c>DisableTestParallelization</c> in <c>GlobalUsings.cs</c>.
/// </summary>
public sealed class StreamingDisconnectTests(StreamingDisconnectAppFixture fixture) : IClassFixture<StreamingDisconnectAppFixture>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Caller_abort_mid_stream_is_not_logged_as_unhandled_error()
    {
        StaticLogCapture.Sink.Clear();
        var upstreamStream = new CallerAbortStream("data: {\"delta\":\"hi\"}\n\n");
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(upstreamStream)
            {
                Headers = { ContentType = new("text/event-stream") }
            }
        };

        HttpClient client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = Json("""{"model":"gpt5.4","stream":true}""")
        };

        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Stream body = await response.Content.ReadAsStreamAsync(Ct);

        var buffer = new byte[64];
        int read = await body.ReadAsync(buffer, Ct);
        read.ShouldBeGreaterThan(0); // first SSE chunk reached the caller — we are genuinely mid-stream

        // Abort mid-stream, like a Codex/Claude client cancelling an in-flight SSE request. Dropping the
        // connection (disposing the response) is what makes TestServer fire context.RequestAborted.
        body.Dispose();
        response.Dispose();

        // The forwarder's blocked read is cancelled by RequestAborted — this is the exact path that threw
        // the unhandled OperationCanceledException before the fix.
        await upstreamStream.Completed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        upstreamStream.RequestAbortedObserved.ShouldBeTrue();

        // Wait for the request-logging middleware to emit its completion event for the aborted request.
        LogEvent completion = await WaitForRequestCompletionAsync();

        // The fix returns quietly on caller abort, so the request completes without an unhandled exception:
        // the completion event is not Error level and carries no exception. Before the fix this was logged at
        // Error with a TaskCanceledException stack trace.
        (completion.Level >= LogEventLevel.Error).ShouldBeFalse();
        completion.Exception.ShouldBeNull();

        // Defence in depth: nothing anywhere logged the caller-abort as an error with a cancellation/IO exception.
        bool loggedAbortAsError = StaticLogCapture.Sink.Events.Any(e =>
            e.Level >= LogEventLevel.Error && e.Exception is OperationCanceledException or IOException);
        loggedAbortAsError.ShouldBeFalse();
    }

    private async Task<LogEvent> WaitForRequestCompletionAsync()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            LogEvent? completion = StaticLogCapture.Sink.Events.LastOrDefault(IsRequestCompletion);
            if (completion is not null)
            {
                return completion;
            }

            await Task.Delay(100, Ct);
        }

        throw new InvalidOperationException("No request-logging completion event was captured for the aborted request.");
    }

    private static bool IsRequestCompletion(LogEvent e) =>
        e.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? source) &&
        source.ToString().Contains("RequestLoggingMiddleware", StringComparison.Ordinal);

    // Server-side upstream content: emits one SSE chunk, then blocks on the forwarder's CancellationToken
    // (which is context.RequestAborted). A client abort therefore interrupts the blocked read mid-stream.
    private sealed class CallerAbortStream(string firstChunk) : Stream
    {
        private readonly byte[] _firstChunk = Encoding.UTF8.GetBytes(firstChunk);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _served;

        public Task Completed => _completed.Task;
        public bool RequestAbortedObserved { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_served)
            {
                _served = true;
                _firstChunk.AsSpan().CopyTo(buffer.Span);
                return _firstChunk.Length;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                RequestAbortedObserved = true;
                throw;
            }

            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            _completed.TrySetResult();
            base.Dispose(disposing);
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

/// <summary>Process-global in-memory Serilog sink the abort test asserts against (see fixture for wiring).</summary>
public static class StaticLogCapture
{
    public static CapturingSink Sink { get; } = new();
}

public sealed class CapturingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = new();
    private readonly Lock _gate = new();

    public IReadOnlyList<LogEvent> Events
    {
        get { lock (_gate) { return _events.ToArray(); } }
    }

    public void Clear()
    {
        lock (_gate) { _events.Clear(); }
    }

    public void Emit(LogEvent logEvent)
    {
        lock (_gate) { _events.Add(logEvent); }
    }
}

public sealed class StreamingDisconnectAppFixture : WebApplicationFactory<HostApp::Program>
{
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Imposter:Providers:0:Name"] = "openai-official",
        ["Imposter:Providers:0:Dialect"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:0:Secret"] = "openai-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        ["Imposter:Providers:1:Name"] = "opencode-go",
        ["Imposter:Providers:1:Dialect"] = "openai",
        ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:1:Secret"] = "opencode-key",
        ["Imposter:Providers:1:AuthScheme"] = "ApiKey",
        ["Imposter:Providers:1:OpenAiUpstreamApi"] = "chat_completions",
        ["Imposter:Providers:1:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:1:Models:0:To"] = "grok-code",
        ["Imposter:Providers:1:Models:0:Caching"] = "true",
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

    // Program's UseSerilogRequestLogging writes the request-completion event (where the unhandled streaming
    // exception surfaced) through the static Log.Logger. Redirect that global to the capture sink AFTER the
    // host is built (Program's UseSerilog sets Log.Logger during the build, so this must run last).
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
        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential>(new OpenAiCredential("unused", "cipher", CredentialAuthScheme.Bearer, null));
    }
}
