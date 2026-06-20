using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.Endpoints;

/// <summary>
/// Maps the inbound dialect endpoints. The router is a transparent same-dialect proxy: clients point their
/// base URL at a dialect prefix (<c>/openai</c> or <c>/anthropic</c>) and the segment after the prefix is the
/// upstream path, forwarded verbatim with the inbound method. Each handler reads the body, asks the
/// Application to plan the route (model rewrite + caching, or default passthrough for body-less requests),
/// forwards via Infrastructure, and streams the upstream response back unbuffered. Legacy unprefixed
/// <c>POST /v1/*</c> completion routes are kept for back-compat. All HTTP concerns live here;
/// Application/Infrastructure stay transport-agnostic.
/// </summary>
internal static class RoutingEndpoints
{
    private const string LoggerCategory = "SmoothLlmImposter.Routing";

    public static void MapImposterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Dialect-prefixed transparent proxy (any HTTP method). The prefix selects the dialect — which
        // disambiguates shared paths like /v1/models that are identical across OpenAI and Anthropic — and the
        // captured tail is forwarded verbatim, so /v1/models, /v1/responses, /v1/messages/count_tokens, etc.
        // all proxy without a per-route mapping.
        app.Map("/openai/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, NormalizeUpstreamPath(upstreamPath), router, forwarder, responseTransformer, errors, loggerFactory));

        // Anthropic model discovery is answered LOCALLY from the route catalogue (distinct union of configured
        // `to` targets), not forwarded upstream (HLD 005, Anthropic scope). This specific GET route takes
        // precedence over the /anthropic catch-all for GET; non-GET methods on the same path do not match here
        // and fall through to the catch-all transparent passthrough, which is exactly the intended scope (LADR-03).
        app.MapGet("/anthropic/v1/models", (HttpContext ctx, IAnthropicModelCatalogResponder responder, ILoggerFactory loggerFactory) =>
            WriteModelCatalogAsync(ctx, ApiDialect.Anthropic, responder.BuildModelsResponse(), loggerFactory));

        app.Map("/anthropic/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, NormalizeUpstreamPath(upstreamPath), router, forwarder, responseTransformer, errors, loggerFactory));

        // Legacy unprefixed completion routes (POST only). The inbound path is the upstream path. Unprefixed
        // /v1/models is intentionally NOT mapped here — it is dialect-ambiguous; use the /openai or /anthropic prefix.
        app.MapPost("/v1/chat/completions", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/chat/completions", router, forwarder, responseTransformer, errors, loggerFactory));

        app.MapPost("/v1/responses", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/responses", router, forwarder, responseTransformer, errors, loggerFactory));

        app.MapPost("/v1/messages", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, "/v1/messages", router, forwarder, responseTransformer, errors, loggerFactory));
    }

    // The {**upstreamPath} catch-all captures the tail WITHOUT a leading slash and excludes the query string.
    // Restore the leading slash so it appends cleanly to the provider base URL (BaseUrl + path).
    private static string NormalizeUpstreamPath(string? upstreamPath) =>
        string.IsNullOrEmpty(upstreamPath) ? "/" : "/" + upstreamPath;

    private static async Task HandleAsync(
        HttpContext context,
        ApiDialect dialect,
        string upstreamPath,
        IImposterRouter router,
        IUpstreamForwarder forwarder,
        IChatToResponsesTransformer responseTransformer,
        IErrorResponseFactory errors,
        ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(LoggerCategory);
        CancellationToken cancellationToken = context.RequestAborted;

        string requestBody = await ReadBodyAsync(context, cancellationToken);

        LogInboundRequest(logger, context, dialect, upstreamPath, requestBody);

        RoutePlan plan;
        try
        {
            // A body carries a model → imposter/default resolution + transform. No body (e.g. GET /v1/models)
            // → passthrough to the dialect default, since there is no model to match on.
            plan = string.IsNullOrWhiteSpace(requestBody)
                ? await router.PlanPassthroughAsync(dialect, cancellationToken)
                : await router.PlanAsync(dialect, requestBody, cancellationToken);
        }
        catch (RoutingException ex)
        {
            await WriteErrorAsync(context, dialect, errors, ex.StatusCode, ex.Message, ErrorTypeFor(ex.StatusCode), cancellationToken);
            return;
        }

        bool translateChatToResponses = ShouldTranslateChatToResponses(dialect, upstreamPath, plan);
        HttpResponseMessage upstream;
        try
        {
            upstream = await forwarder.SendAsync(
                plan.Decision,
                plan.CredentialOverride,
                dialect,
                HttpMethod.Parse(context.Request.Method),
                string.IsNullOrEmpty(plan.TransformedBody) ? null : plan.TransformedBody,
                translateChatToResponses ? "/v1/chat/completions" : upstreamPath,
                context.Request.QueryString.Value,
                CaptureCallerHeaders(context),
                cancellationToken);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // caller disconnected; nothing to write.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Upstream forward to provider {Provider} failed", plan.Decision.Provider.Name);
            await WriteErrorAsync(
                context,
                dialect,
                errors,
                StatusCodes.Status502BadGateway,
                $"Upstream request to '{plan.Decision.Provider.Name}' failed: {ex.Message}",
                "upstream_error",
                cancellationToken);
            return;
        }

        using (upstream)
        {
            try
            {
                if (translateChatToResponses && !upstream.IsSuccessStatusCode)
                {
                    string upstreamError = await upstream.Content.ReadAsStringAsync(cancellationToken);
                    await WriteErrorAsync(
                        context,
                        dialect,
                        errors,
                        (int)upstream.StatusCode,
                        string.IsNullOrWhiteSpace(upstreamError) ? $"Upstream request to '{plan.Decision.Provider.Name}' failed." : upstreamError,
                        "upstream_error",
                        cancellationToken);
                    return;
                }

                await StreamResponseAsync(context, upstream, responseTransformer, translateChatToResponses, cancellationToken);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                // Caller disconnected mid-stream. The status line and partial SSE are already on the wire, so
                // there is nothing left to write and nothing to retry — this mirrors the forward-path guard above.
                // A mid-stream abort surfaces as either a cooperative cancel (OperationCanceledException, which
                // TaskCanceledException derives from) or a socket reset on flush (IOException, e.g. Kestrel's
                // ConnectionResetException); both are benign once the caller is gone, so return quietly instead of
                // logging an unhandled error + stack trace. The filter is gated on RequestAborted, so a genuine
                // streaming failure while the caller is still connected still propagates and is logged.
                return;
            }
        }
    }

    // Auth headers whose secret value is masked in the Debug request dump so real keys never reach the log sink.
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "x-api-key",
    };

    // Debug-only dump of the full inbound request (method, path, query, every header, raw body). Off by default
    // — the SmoothLlmImposter.Routing logger sits at Information unless its minimum level is overridden to Debug.
    // The IsEnabled guard keeps it free when disabled (no header/body string is built). Auth secrets are masked.
    private static void LogInboundRequest(ILogger logger, HttpContext context, ApiDialect dialect, string upstreamPath, string requestBody)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var headers = new StringBuilder();
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers)
        {
            string value = SensitiveHeaders.Contains(header.Key)
                ? MaskSecretHeader(header.Value.ToString())
                : header.Value.ToString();
            headers.Append("\n  ").Append(header.Key).Append(": ").Append(value);
        }

        logger.LogDebug(
            "Inbound {Dialect} request {Method} {Path}{Query}\nHeaders:{Headers}\nBody: {Body}",
            dialect,
            context.Request.Method,
            upstreamPath,
            context.Request.QueryString.Value,
            headers.ToString(),
            string.IsNullOrEmpty(requestBody) ? "(empty)" : requestBody);
    }

    // Preserve the auth scheme prefix (e.g. "Bearer ") and the secret's last 4 chars; mask the rest. Short
    // secrets (≤4 chars) are fully masked so nothing recoverable is logged.
    private static string MaskSecretHeader(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        int spaceIndex = value.IndexOf(' ');
        string scheme = spaceIndex > 0 ? value[..(spaceIndex + 1)] : string.Empty;
        string secret = spaceIndex > 0 ? value[(spaceIndex + 1)..] : value;
        string tail = secret.Length > 4 ? secret[^4..] : string.Empty;

        return $"{scheme}***{tail}";
    }

    private static CallerHeaders CaptureCallerHeaders(HttpContext context)
    {
        // Capture the full inbound header set at the Host edge so the forwarder can proxy it through
        // verbatim (HttpContext must not leak into Application/Infrastructure).
        var items = new List<KeyValuePair<string, IReadOnlyList<string>>>(context.Request.Headers.Count);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in context.Request.Headers)
        {
            items.Add(new(header.Key, header.Value.Select(value => value ?? string.Empty).ToArray()));
        }

        return new CallerHeaders(items);
    }

    private static bool ShouldTranslateChatToResponses(ApiDialect dialect, string upstreamPath, RoutePlan plan) =>
        dialect == ApiDialect.OpenAi &&
        plan.Decision.IsImposter &&
        plan.Decision.Provider.OpenAiUpstreamApi == OpenAiUpstreamApi.ChatCompletions &&
        upstreamPath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task StreamResponseAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        IChatToResponsesTransformer responseTransformer,
        bool translateChatToResponses,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;

        if (translateChatToResponses)
        {
            await StreamTranslatedChatResponseAsync(context, upstream, responseTransformer, cancellationToken);
            return;
        }

        if (upstream.Content.Headers.ContentType is { } contentType)
        {
            context.Response.ContentType = contentType.ToString();
        }

        // Unbuffered so SSE chunks reach the caller as they arrive.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await upstreamStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task StreamTranslatedChatResponseAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        IChatToResponsesTransformer responseTransformer,
        CancellationToken cancellationToken)
    {
        // Unbuffered so translated SSE frames reach the caller as their source Chat chunks arrive.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        if (IsEventStream(upstream))
        {
            context.Response.ContentType = "text/event-stream";
            await using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
            await foreach (string frame in responseTransformer.TransformStreamingAsync(ReadLinesAsync(upstreamStream, cancellationToken), cancellationToken))
            {
                await context.Response.WriteAsync(frame, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            return;
        }

        context.Response.ContentType = "application/json";
        string chatCompletionJson = await upstream.Content.ReadAsStringAsync(cancellationToken);
        await context.Response.WriteAsync(responseTransformer.TransformNonStreaming(chatCompletionJson), cancellationToken);
    }

    private static bool IsEventStream(HttpResponseMessage upstream) =>
        string.Equals(upstream.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            yield return line;
        }
    }

    // Writes a catalogue-synthesized discovery body (e.g. GET /anthropic/v1/models) the Application built from
    // configuration. No upstream forward and no credential read happen on this path (HLD 005 NFR-03) — the Host
    // only recognizes the case and serializes the already-built string (LADR-04).
    private static async Task WriteModelCatalogAsync(HttpContext context, ApiDialect dialect, string body, ILoggerFactory loggerFactory)
    {
        loggerFactory.CreateLogger(LoggerCategory).LogInformation(
            "Served {Dialect} /v1/models locally from the configured route catalogue", dialect);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body, context.RequestAborted);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        ApiDialect dialect,
        IErrorResponseFactory errors,
        int statusCode,
        string message,
        string type,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(errors.Create(dialect, message, type), cancellationToken);
    }

    private static string ErrorTypeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status403Forbidden => "permission_error",
        StatusCodes.Status404NotFound => "not_found_error",
        StatusCodes.Status500InternalServerError => "api_error",
        _ => "invalid_request_error"
    };
}
