# LADR-03: The timeout bounds response headers only, never the streaming body

**Status:** Accepted

## Context

`UpstreamForwarder` sends with `HttpCompletionOption.ResponseHeadersRead` and the named client
keeps `Timeout.InfiniteTimeSpan`, because SSE responses routinely outlive any sane request
timeout. The Polly timeout therefore stops applying the moment response headers arrive.

## Decision

`TimeoutSeconds` is documented and named as a **header** timeout. It does not bound the body,
and no attempt is made to make it do so.

## Alternatives Considered

- **Extend it into a total-request budget** — rejected. It would kill long, healthy SSE streams,
  which is the traffic this router exists to carry.
- **Add a streaming idle timeout under the same option** — rejected *for now*, not on principle.
  It is a genuinely different control (idle gap vs total wait) with its own failure mode, and it
  belongs to the mid-stream-failure work that already owns the terminal-error-frame path. Folding
  two semantics into one number would make both unclear.

## Consequences

- An upstream that answers headers quickly and then stalls is **not** covered by this option;
  it ends via caller disconnect or the mid-stream `IOException` path.
- The XML docs on `ProviderOptions.TimeoutSeconds` state the scope explicitly, because the
  obvious reading of "timeout" is "total", and the gap between those two is where support
  questions come from.
