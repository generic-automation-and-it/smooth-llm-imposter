using System.Buffers;
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
        app.Map("/openai/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, NormalizeUpstreamPath(upstreamPath), router, forwarder, errors, loggerFactory));

        app.Map("/anthropic/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, NormalizeUpstreamPath(upstreamPath), router, forwarder, errors, loggerFactory));

        // Legacy unprefixed completion routes (POST only). The inbound path is the upstream path. Unprefixed
        // /v1/models is intentionally NOT mapped here — it is dialect-ambiguous; use the /openai or /anthropic prefix.
        app.MapPost("/v1/chat/completions", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/chat/completions", router, forwarder, errors, loggerFactory));

        app.MapPost("/v1/responses", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/responses", router, forwarder, errors, loggerFactory));

        app.MapPost("/v1/messages", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, "/v1/messages", router, forwarder, errors, loggerFactory));
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
        IErrorResponseFactory errors,
        ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(LoggerCategory);
        CancellationToken cancellationToken = context.RequestAborted;

        string requestBody = await ReadBodyAsync(context, cancellationToken);

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

        HttpResponseMessage upstream;
        try
        {
            upstream = await forwarder.SendAsync(
                plan.Decision,
                plan.CredentialOverride,
                dialect,
                HttpMethod.Parse(context.Request.Method),
                string.IsNullOrEmpty(plan.TransformedBody) ? null : plan.TransformedBody,
                upstreamPath,
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
            await StreamResponseAsync(context, upstream, cancellationToken);
        }
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

    private static async Task<string> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task StreamResponseAsync(HttpContext context, HttpResponseMessage upstream, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;

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
