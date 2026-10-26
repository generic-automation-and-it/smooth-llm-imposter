using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Application.Features.Routing.Decisions;
using SmoothLlmImposter.Application.Features.Routing.Optimization;
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
        app.MapGet("/health", () => Results.Ok(new HealthResponse("ok")));

        // Local model discovery for the OpenAI dialect (HLD 005). This specific GET route outranks the
        // /openai/{**upstreamPath} catch-all for GET, while every other method on the same path still falls
        // through to the catch-all passthrough — exactly realizing LADR-03 (OpenAI + GET + /v1/models only).
        // The response is synthesized from configuration: no body read, no forwarder call, no credential seam.
        app.MapGet("/openai/v1/models", (IModelCatalogResponder responder, ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(LoggerCategory)
                .LogDebug("Answered GET /openai/v1/models locally from the OpenAI route catalogue");
            return Results.Text(responder.BuildOpenAiModelsResponse(), "application/json");
        });

        // Dialect-prefixed transparent proxy (any HTTP method). The prefix selects the dialect — which
        // disambiguates shared paths like /v1/models that are identical across OpenAI and Anthropic — and the
        // captured tail is forwarded verbatim, so /v1/models, /v1/responses, /v1/messages/count_tokens, etc.
        // all proxy without a per-route mapping.
        app.Map("/openai/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, IWhoMessageResponder whoResponder, IOptimizationCoordinator optimizationCoordinator, IToolCatalogOrderer toolOrderer, ISessionTranslationDictionary sessionTranslation, IGithubCopilotConnectionRegistry copilotRegistry, IOptions<ImposterOptions> imposterOptions, IAnthropicChatBridge bridge, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, NormalizeUpstreamPath(upstreamPath), router, forwarder, responseTransformer, errors, whoResponder, optimizationCoordinator, toolOrderer, sessionTranslation, copilotRegistry, imposterOptions.Value, bridge, loggerFactory));

        // Anthropic model discovery is answered LOCALLY from the route catalogue (distinct union of configured
        // `to` targets), not forwarded upstream (HLD 005, Anthropic scope). This specific GET route takes
        // precedence over the /anthropic catch-all for GET; non-GET methods on the same path do not match here
        // and fall through to the catch-all transparent passthrough, which is exactly the intended scope (LADR-03).
        app.MapGet("/anthropic/v1/models", (IAnthropicModelCatalogResponder responder, ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger(LoggerCategory)
                .LogDebug("Answered GET /anthropic/v1/models locally from the Anthropic route catalogue");
            return Results.Text(responder.BuildModelsResponse(), "application/json");
        });

        app.Map("/anthropic/{**upstreamPath}", (string? upstreamPath, HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, IWhoMessageResponder whoResponder, IOptimizationCoordinator optimizationCoordinator, IToolCatalogOrderer toolOrderer, ISessionTranslationDictionary sessionTranslation, IGithubCopilotConnectionRegistry copilotRegistry, IOptions<ImposterOptions> imposterOptions, IAnthropicChatBridge bridge, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, NormalizeUpstreamPath(upstreamPath), router, forwarder, responseTransformer, errors, whoResponder, optimizationCoordinator, toolOrderer, sessionTranslation, copilotRegistry, imposterOptions.Value, bridge, loggerFactory));

        // Legacy unprefixed completion routes (POST only). The inbound path is the upstream path. Unprefixed
        // /v1/models is intentionally NOT mapped here — it is dialect-ambiguous; use the /openai or /anthropic prefix.
        app.MapPost("/v1/chat/completions", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, IWhoMessageResponder whoResponder, IOptimizationCoordinator optimizationCoordinator, IToolCatalogOrderer toolOrderer, ISessionTranslationDictionary sessionTranslation, IGithubCopilotConnectionRegistry copilotRegistry, IOptions<ImposterOptions> imposterOptions, IAnthropicChatBridge bridge, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/chat/completions", router, forwarder, responseTransformer, errors, whoResponder, optimizationCoordinator, toolOrderer, sessionTranslation, copilotRegistry, imposterOptions.Value, bridge, loggerFactory));

        app.MapPost("/v1/responses", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, IWhoMessageResponder whoResponder, IOptimizationCoordinator optimizationCoordinator, IToolCatalogOrderer toolOrderer, ISessionTranslationDictionary sessionTranslation, IGithubCopilotConnectionRegistry copilotRegistry, IOptions<ImposterOptions> imposterOptions, IAnthropicChatBridge bridge, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.OpenAi, "/v1/responses", router, forwarder, responseTransformer, errors, whoResponder, optimizationCoordinator, toolOrderer, sessionTranslation, copilotRegistry, imposterOptions.Value, bridge, loggerFactory));

        app.MapPost("/v1/messages", (HttpContext ctx, IImposterRouter router, IUpstreamForwarder forwarder, IChatToResponsesTransformer responseTransformer, IErrorResponseFactory errors, IWhoMessageResponder whoResponder, IOptimizationCoordinator optimizationCoordinator, IToolCatalogOrderer toolOrderer, ISessionTranslationDictionary sessionTranslation, IGithubCopilotConnectionRegistry copilotRegistry, IOptions<ImposterOptions> imposterOptions, IAnthropicChatBridge bridge, ILoggerFactory loggerFactory) =>
            HandleAsync(ctx, ApiDialect.Anthropic, "/v1/messages", router, forwarder, responseTransformer, errors, whoResponder, optimizationCoordinator, toolOrderer, sessionTranslation, copilotRegistry, imposterOptions.Value, bridge, loggerFactory));
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
        IWhoMessageResponder whoResponder,
        IOptimizationCoordinator optimizationCoordinator,
        IToolCatalogOrderer toolOrderer,
        ISessionTranslationDictionary sessionTranslation,
        IGithubCopilotConnectionRegistry copilotRegistry,
        ImposterOptions imposterOptions,
        IAnthropicChatBridge bridge,
        ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger(LoggerCategory);
        CancellationToken cancellationToken = context.RequestAborted;

        string requestBody = await ReadBodyAsync(context, cancellationToken);
        CallerHeaders callerHeaders = CaptureCallerHeaders(context);

        LogInboundRequest(logger, context, dialect, upstreamPath, requestBody);

        RoutePlan plan;
        try
        {
            requestBody = toolOrderer.Order(dialect, upstreamPath, requestBody, callerHeaders);

            // A body carries a model → imposter/default resolution + transform. No body (e.g. GET /v1/models)
            // → passthrough to the dialect default, since there is no model to match on.
            // Caller headers are captured once at the Host edge and shared by planning (session resolve)
            // and forwarding (header relay + optional session stamp).
            plan = string.IsNullOrWhiteSpace(requestBody)
                ? await router.PlanPassthroughAsync(dialect, callerHeaders, cancellationToken)
                : await router.PlanAsync(dialect, requestBody, callerHeaders, cancellationToken);
        }
        catch (RoutingException ex)
        {
            await WriteErrorAsync(context, dialect, errors, ex.StatusCode, ex.Message, ErrorTypeFor(ex.StatusCode), cancellationToken);
            return;
        }

        // Snapshot of ImposterOptions for the duration of this request (IOptions<T> is effectively
        // immutable post-startup, so per-request and startup snapshots are equivalent).
        // The WhoMessage.Enabled flag is captured at startup; flipping IMPOSTER_WHO_MESSAGE_ENABLED
        // requires a host restart (LADR-04). IOptionsMonitor is not used here to keep the toggle
        // semantics consistent with every other Imposter option.
        // HLD 010: in-band routing probe. When enabled and the body triggers the responder,
        // short-circuit before the forwarder — zero upstream HTTP calls on match. The feature gate
        // skips the responder entirely when disabled, so the forward path stays byte-identical (NFR-01).
        // TODO(perf): whoResponder.TryBuildResponse re-parses the body that PlanAsync has already
        // parsed. Intentional per the HLD 010 "pure predicate" seam so the responder stays
        // transport-agnostic; revisit once a parsed-body seam is added (one JsonDocument.Parse
        // per chat-completion request today).
        if (!imposterOptions.WhoMessage.Enabled)
        {
            logger.LogDebug("WhoMessage feature is disabled — forwarding without intercept");
        }
        else if (whoResponder.TryBuildResponse(dialect, requestBody, plan, out string? whoResponseJson))
        {
            logger.LogDebug("Short-circuited who-message probe for {Dialect} model '{Model}'", dialect, plan.InboundModel);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            try
            {
                await context.Response.WriteAsync(whoResponseJson!, cancellationToken);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or IOException)
            {
                // Caller disconnected between the feature-gate check and WriteAsync. The status line may
                // already be on the wire; there is nothing left to write and nothing to retry. Mirrors the
                // forward-path guard (lines below) for the same scenario.
                return;
            }
            return;
        }

        // HLD 010, LADR-06: session translation. When the caller's resolved session identity
        // has a mapping in the translation dictionary (minted by a prior --newsession), replace
        // it with the synthetic id before forwarding. Passthrough and un-opted-in routes stay
        // byte-identical (NFR-01) — the dictionary is only consulted when the plan has a
        // stampable session identity. For passthrough routes, `plan.SessionIdentity.HasValue` is
        // false, so the `imposterOptions.WhoMessage.Enabled` gate is naturally bypassed even
        // though the feature toggle is checked first.
        if (imposterOptions.WhoMessage.Enabled &&
            plan.SessionIdentity.HasValue &&
            sessionTranslation.TryTranslate(plan.SessionIdentity.Value!, out string? translated))
        {
            logger.LogDebug(
                "Translating session identity '{Source}' to synthetic '{Synthetic}' for {Dialect} forward",
                plan.SessionIdentity.LogToken,
                translated,
                dialect);
            // Source describes the origin path of the request's session identity
            // (how the caller's value was obtained), not the current value's provenance.
            // The translated value is synthetic, but Source is preserved so log consumers
            // can trace the request back to its inbound session-resolution path.
            plan = plan with { SessionIdentity = new SessionIdentity(translated, plan.SessionIdentity.Source) };
        }

        // HLD 016: a matched Anthropic imposter route whose provider opted into
        // AnthropicUpstreamApi=chat_completions is bridged. The mapper runs after planning/model rename and
        // before the optimization coordinator, so strategies see the Chat body.
        bool bridgedProvider = IsBridgedProvider(dialect, plan);
        AnthropicChatCall? bridgedCall = null;

        if (bridgedProvider)
        {
            if (IsUnsupportedBridgedPath(upstreamPath))
            {
                await WriteErrorAsync(
                    context,
                    dialect,
                    errors,
                    StatusCodes.Status404NotFound,
                    $"Not found: {context.Request.Method} {upstreamPath}",
                    "not_found_error",
                    cancellationToken);
                return;
            }

            bool isMessages = string.Equals(upstreamPath, "/v1/messages", StringComparison.Ordinal);
            bool isCountTokens = string.Equals(upstreamPath, "/v1/messages/count_tokens", StringComparison.Ordinal);
            if ((isMessages || isCountTokens) && !HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.Headers.Allow = "POST";
                await WriteErrorAsync(
                    context,
                    dialect,
                    errors,
                    StatusCodes.Status405MethodNotAllowed,
                    "Method Not Allowed",
                    "invalid_request_error",
                    cancellationToken);
                return;
            }

            if (isCountTokens)
            {
                try
                {
                    string countBody = bridge.CountTokens(requestBody, plan.Decision.Provider.AnthropicChat);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(countBody, cancellationToken);
                }
                catch (AnthropicChatRequestException ex)
                {
                    await WriteErrorAsync(context, dialect, errors, StatusCodes.Status400BadRequest, ex.Message, "invalid_request_error", cancellationToken);
                }

                return;
            }

            if (isMessages)
            {
                try
                {
                    bridgedCall = bridge.MapRequest(plan.TransformedBody, plan.InboundModel, plan.Decision.Provider.AnthropicChat);
                    plan = plan with { TransformedBody = bridgedCall.ChatBody };
                }
                catch (AnthropicChatRequestException ex)
                {
                    await WriteErrorAsync(context, dialect, errors, StatusCodes.Status400BadRequest, ex.Message, "invalid_request_error", cancellationToken);
                    return;
                }
            }
        }

        bool translateChatToResponses = ShouldTranslateChatToResponses(dialect, upstreamPath, plan);
        string effectiveUpstreamPath = bridgedCall is not null
            ? "/v1/chat/completions"
            : translateChatToResponses ? "/v1/chat/completions" : upstreamPath;
        HttpResponseMessage upstream;
        try
        {
            plan = plan with
            {
                TransformedBody = await optimizationCoordinator.OptimizeAsync(
                    new OptimizationRequest(effectiveUpstreamPath, plan.TransformedBody, callerHeaders, requestBody),
                    cancellationToken)
            };
            upstream = await forwarder.SendAsync(
                plan.Decision,
                plan.CredentialOverride,
                dialect,
                HttpMethod.Parse(context.Request.Method),
                string.IsNullOrEmpty(plan.TransformedBody) ? null : plan.TransformedBody,
                effectiveUpstreamPath,
                // HLD 016 spec 01 section 15 / 04 E01: a bridged route never forwards the inbound query string.
                bridgedCall is null ? context.Request.QueryString.Value : null,
                callerHeaders,
                plan.SessionIdentity,
                cancellationToken,
                useHeaderAllowlist: bridgedCall is not null,
                allowlistAccept: bridgedCall is null ? null : bridgedCall.Stream ? "text/event-stream" : "application/json");

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // caller disconnected; nothing to write.
        }
        catch (RoutingException ex)
        {
            // Refused before any credential left the process (e.g. a Copilot path the profile does not serve).
            await WriteErrorAsync(context, dialect, errors, ex.StatusCode, ex.Message, ErrorTypeFor(ex.StatusCode), cancellationToken);
            return;
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

        // Managed Copilot credential revocation is revision-scoped (AC11): an upstream 401 invalidates only the
        // connection revision this request was planned against, never replays the billable POST, and never
        // treats an ordinary 403/model-policy failure as account revocation. The durable row is retained (D3).
        if (upstream.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            plan.Copilot is { } copilot &&
            copilotRegistry.InvalidateIfCurrent(copilot.Prefix, copilot.Revision))
        {
            logger.LogWarning(
                "Upstream rejected the GitHub Copilot credential for connection prefix {Prefix} (revision {Revision}); routing now fails closed until a new login.",
                copilot.Prefix,
                copilot.Revision);
        }

        using (upstream)
        {
            try
            {
                if (bridgedCall is not null)
                {
                    await HandleBridgedResponseAsync(
                        context,
                        upstream,
                        plan,
                        bridgedCall,
                        bridge,
                        errors,
                        logger,
                        cancellationToken);
                    return;
                }

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
                // streaming failure while the caller is still connected still propagates to the handler below.
                return;
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                // The relay itself broke while the caller was still connected — most often the upstream ending a
                // chunked body without its terminating chunk (HttpIOException/ResponseEnded), e.g. a provider
                // gateway culling a stream that went quiet. Letting this escape reaches Kestrel's error handler
                // after the response has started, which can only log an unhandled stack trace and cut the socket,
                // so the client sees an indistinguishable truncation. End the response deliberately instead.
                await EndFailedStreamAsync(
                    context,
                    logger,
                    errors,
                    StreamErrorFramingFor(dialect, upstreamPath, translateChatToResponses),
                    dialect,
                    plan.Decision.Provider.Name,
                    ex,
                    cancellationToken);
            }
        }
    }

    // Which SSE shape is already on the wire. Dialect alone is insufficient: an OpenAI caller is mid-Responses-stream
    // when it asked for /responses (whether forwarded verbatim or downgraded to Chat and translated back), and
    // mid-Chat-stream otherwise.
    private static StreamErrorFraming StreamErrorFramingFor(ApiDialect dialect, string upstreamPath, bool translateChatToResponses)
    {
        if (dialect == ApiDialect.Anthropic)
        {
            return StreamErrorFraming.Anthropic;
        }

        return translateChatToResponses || upstreamPath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
            ? StreamErrorFraming.OpenAiResponses
            : StreamErrorFraming.OpenAiChat;
    }

    private static async Task EndFailedStreamAsync(
        HttpContext context,
        ILogger logger,
        IErrorResponseFactory errors,
        StreamErrorFraming framing,
        ApiDialect dialect,
        string providerName,
        Exception ex,
        CancellationToken cancellationToken)
    {
        // Neutral wording: the same exception types cover an upstream that died and a caller socket that failed on
        // flush without RequestAborted having fired yet. The inner message ("The response ended prematurely.")
        // carries the discriminating detail, and it reaches the client too.
        string message = $"Streaming response from '{providerName}' failed mid-stream: {ex.Message}";
        logger.LogError(ex, "Streaming relay for provider {Provider} failed mid-stream", providerName);

        try
        {
            // Nothing on the wire yet (the failure hit the first read), so a normal dialect-shaped error body is
            // still writable and strictly more useful than an SSE frame.
            if (!context.Response.HasStarted)
            {
                await WriteErrorAsync(context, dialect, errors, StatusCodes.Status502BadGateway, message, "upstream_error", cancellationToken);
                return;
            }

            // A partially written non-SSE body (e.g. a truncated JSON object) cannot be made well-formed by
            // appending anything, so the log above is all this path can offer.
            if (!IsEventStreamResponse(context))
            {
                return;
            }

            // No trailing [DONE]/message_stop: that sentinel means "completed normally", and a client that ignores
            // the error frame must still observe an abnormal end rather than a silently truncated answer.
            await context.Response.WriteAsync(errors.CreateStreamErrorFrame(framing, message, "upstream_error"), cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception writeFailure)
        {
            // Deliberately unfiltered. This is a best-effort courtesy write on a connection that is already known to
            // be broken, so every failure mode has the same correct response: give up. The expected ones are the
            // caller going away between the relay failing and this write (OperationCanceledException/IOException,
            // when RequestAborted had not yet fired for the filter above), but a write to a half-dead connection can
            // also surface as ObjectDisposedException or InvalidOperationException, and letting either escape would
            // land back in the post-HasStarted Kestrel path this whole handler exists to avoid. The original cause
            // is already logged above; this one is Debug because it is a consequence, not a new fault.
            logger.LogDebug(writeFailure, "Could not deliver the terminal error response for provider {Provider}", providerName);
        }
    }

    private static bool IsEventStreamResponse(HttpContext context) =>
        context.Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;

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
            string value = SensitiveHeaderNames.Values.Contains(header.Key)
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

    private static bool IsBridgedProvider(ApiDialect dialect, RoutePlan plan) =>
        dialect == ApiDialect.Anthropic &&
        plan.Decision.IsImposter &&
        plan.Decision.Provider.AnthropicChat.IsBridged;

    // Endpoints the bridge does not implement (HLD 016 spec 04 section 1.1 / D16).
    private static readonly string[] UnsupportedBridgedPrefixes =
    [
        "/v1/messages/batches",
        "/v1/files",
        "/v1/skills",
        "/v1/complete",
        "/v1/organizations/",
        "/v1/oauth/",
        "/api/"
    ];

    private static bool IsUnsupportedBridgedPath(string path) =>
        UnsupportedBridgedPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.Ordinal) ||
            string.Equals(path, prefix.TrimEnd('/'), StringComparison.Ordinal));

    private static async Task HandleBridgedResponseAsync(
        HttpContext context,
        HttpResponseMessage upstream,
        RoutePlan plan,
        AnthropicChatCall call,
        IAnthropicChatBridge bridge,
        IErrorResponseFactory errors,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        string? configuredSecret = plan.CredentialOverride?.Secret ?? plan.Decision.Provider.Secret;
        // Read once: an in-band error on a 2xx reply must forward the same retry headers an HTTP error would.
        (string? retryAfter, string? retryAfterMs) = ReadRetryHeaders(upstream);

        if (!upstream.IsSuccessStatusCode)
        {
            string errorBody = await upstream.Content.ReadAsStringAsync(cancellationToken);
            AnthropicChatReply reply = bridge.MapResponse(
                (int)upstream.StatusCode, errorBody, retryAfter, retryAfterMs, call, configuredSecret);
            await WriteBridgedReplyAsync(context, reply, cancellationToken);
            return;
        }

        if (call.Stream)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            if (IsEventStream(upstream))
            {
                SetBridgedStreamHeaders(context);
                await using Stream upstreamStream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
                await foreach (string frame in bridge.TranslateStreamAsync(
                    ReadLinesAsync(upstreamStream, cancellationToken), call, cancellationToken))
                {
                    await context.Response.WriteAsync(RedactErrorFrame(frame, configuredSecret), cancellationToken);
                    await context.Response.Body.FlushAsync(cancellationToken);
                }

                return;
            }

            // A 2xx non-SSE body for a streaming request is mapped as Chat JSON and replayed as a complete
            // Messages event sequence (spec 03 section 1).
            string json = await upstream.Content.ReadAsStringAsync(cancellationToken);
            AnthropicChatReply mapped = bridge.MapResponse(
                (int)upstream.StatusCode, json, retryAfter, retryAfterMs, call, configuredSecret);
            if (mapped.StatusCode != StatusCodes.Status200OK)
            {
                await WriteBridgedReplyAsync(context, mapped, cancellationToken);
                return;
            }

            SetBridgedStreamHeaders(context);
            foreach (string frame in bridge.ReplayAsStream(mapped, call))
            {
                await context.Response.WriteAsync(frame, cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }

            return;
        }

        string body = await upstream.Content.ReadAsStringAsync(cancellationToken);
        AnthropicChatReply replyBody = bridge.MapResponse(
            (int)upstream.StatusCode, body, retryAfter, retryAfterMs, call, configuredSecret);
        await WriteBridgedReplyAsync(context, replyBody, cancellationToken);
        logger.LogDebug(
            "Mapped bridged {Dialect} reply from provider {Provider} with status {Status}",
            ApiDialect.Anthropic,
            plan.Decision.Provider.Name,
            replyBody.StatusCode);
    }

    // HLD 016 spec 04 section 7.2/10: the configured provider key must never reach the client, including
    // inside an in-band stream error frame. Only error frames are rewritten; model text is never altered.
    private static string RedactErrorFrame(string frame, string? configuredSecret) =>
        !string.IsNullOrEmpty(configuredSecret) &&
        frame.StartsWith("event: error", StringComparison.Ordinal)
            ? frame.Replace(configuredSecret, "***", StringComparison.Ordinal)
            : frame;

    private static void SetBridgedStreamHeaders(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteBridgedReplyAsync(
        HttpContext context,
        AnthropicChatReply reply,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = reply.StatusCode;
        context.Response.ContentType = "application/json";

        if (reply.StatusCode >= 400)
        {
            if (reply.RetryAfter is not null)
            {
                context.Response.Headers["retry-after"] = reply.RetryAfter;
            }

            if (reply.RetryAfterMs is not null)
            {
                context.Response.Headers["retry-after-ms"] = reply.RetryAfterMs;
            }
        }

        await context.Response.WriteAsync(reply.Body, cancellationToken);
    }

    private static (string? RetryAfter, string? RetryAfterMs) ReadRetryHeaders(HttpResponseMessage upstream)
    {
        string? retryAfter = null;
        if (upstream.Headers.TryGetValues("Retry-After", out IEnumerable<string>? retryValues))
        {
            string? value = retryValues.FirstOrDefault();
            if (value is not null && IsValidRetryAfter(value))
            {
                retryAfter = value;
            }
        }

        string? retryAfterMs = null;
        if (upstream.Headers.TryGetValues("retry-after-ms", out IEnumerable<string>? retryMsValues))
        {
            string? value = retryMsValues.FirstOrDefault();
            if (value is not null && int.TryParse(value, out int milliseconds) && milliseconds >= 0)
            {
                retryAfterMs = value;
            }
        }

        return (retryAfter, retryAfterMs);
    }

    private static bool IsValidRetryAfter(string value) =>
        (int.TryParse(value, out int seconds) && seconds >= 0) ||
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out _);

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

/// <summary>
/// Typed <c>/health</c> body. A record (not an anonymous type) so the NativeAOT JSON source
/// generator can provide its metadata via <see cref="Configuration.ImposterJsonContext"/>.
/// The wire shape stays <c>{"status":"ok"}</c>.
/// </summary>
internal sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status);
