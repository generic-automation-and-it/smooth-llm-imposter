using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.Endpoints;

/// <summary>
/// Maps the inbound dialect endpoints. Each handler reads the body, asks the Application to plan the
/// route (model rewrite + caching), forwards via Infrastructure, and streams the upstream response
/// back unbuffered. All HTTP concerns live here; Application/Infrastructure stay transport-agnostic.
/// </summary>
internal static class RoutingEndpoints
{
    private const string LoggerCategory = "SmoothLlmImposter.Routing";

    public static void MapImposterEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapPost("/v1/chat/completions", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, router, forwarder, errors, loggerFactory));

        app.MapPost("/v1/responses", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, router, forwarder, errors, loggerFactory));

        app.MapPost("/v1/messages", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IErrorResponseFactory errors, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, router, forwarder, errors, loggerFactory));
    }

    private static async Task HandleAsync(
        HttpContext context,
        ApiDialect dialect,
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
            plan = await router.PlanAsync(dialect, requestBody, cancellationToken);
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
                plan.TransformedBody,
                context.Request.Path,
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
