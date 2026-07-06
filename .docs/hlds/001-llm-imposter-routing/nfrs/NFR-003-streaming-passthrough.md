# NFR-003 — Streaming Pass-Through

- **Category:** Performance / Latency
- **Status:** Accepted · 2026-06-14

Upstream responses are streamed back **unbuffered**, chunk-by-chunk (SSE), using
`HttpCompletionOption.ResponseHeadersRead` with response buffering disabled and explicit
per-read flushing. The upstream client uses an infinite timeout bounded by the caller's
cancellation token, since SSE streams outlive standard HTTP-client timeouts.
`ResponseHeadersRead` also scopes the upstream retry handler to the response-header boundary:
body-stream failures cannot trigger a replay of an in-flight response.

See [LADR-003 — Infinite client timeout, targeted retry handler](../ladrs/LADR-003-infinite-timeout-no-resilience-handler.md).
