# LADR-02: Carry the per-provider timeout on the request, read it from the resilience context

**Status:** Accepted

## Context

The timeout lives in the Polly resilience pipeline attached to the single named
`imposter-upstream` client. The pipeline is built once at startup; the provider is only known
per request, after routing has resolved. The value therefore has to cross from
`UpstreamForwarder` (which holds the `RouteDecision`) into a `TimeoutGenerator` callback
(which is handed only a `ResilienceContext`).

## Decision

`UpstreamForwarder` stamps the resolved base onto the outbound request:

```csharp
request.Options.Set(DependencyInjection.UpstreamTimeoutSecondsKey, timeoutSeconds);
```

`GetUpstreamTimeoutSeconds` reads it back via `context.GetRequestMessage()`
(`Microsoft.Extensions.Http.Resilience`), falling back to the default when the request is
unavailable, unstamped, or carries a non-positive value.

## Alternatives Considered

- **A named client per provider** (`AddHttpClient($"imposter-upstream-{key}")`) — rejected.
  Providers are runtime-mutable (HLD 008): a provider created through `/admin/providers` after
  startup would have no registered client, so the forwarder would have to fall back or fail.
  It also multiplies handler lifetimes and connection pools per provider for one integer.
- **`AsyncLocal` / ambient state** — rejected. Invisible coupling across a library boundary,
  and it survives into unrelated continuations.
- **Cancel with a linked `CancellationTokenSource` in the forwarder instead of Polly** —
  rejected. The retry strategy retries on `TimeoutRejectedException`; a cancelled token
  surfaces as `OperationCanceledException`, indistinguishable from the caller disconnecting.
  Timing out that way would silently disable retries and could be misreported as a client abort.
- **Bind the value into the pipeline via `IOptionsMonitor` at handler-build time** — rejected.
  The pipeline is per-client, not per-provider; there is no build-time provider identity.

## Consequences

- `UpstreamTimeoutSecondsKey` is `internal` and shared between `DependencyInjection` and
  `UpstreamForwarder` — both are Infrastructure, so no new public surface.
- The stamp is the load-bearing hop: drop it and the option is fully configurable, fully
  visible through `/admin/providers`, and completely inert. It is covered by a dedicated test
  (`UpstreamForwarderTimeoutTests`) for exactly the failure mode HLD 011 shipped with.
