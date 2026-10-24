# LADR-06: Allowlist request mapping; placeholders for history, rejection for intent

**Status:** Draft

## Context

- Strict Chat upstreams reject unknown top-level fields.
- Some Messages constructs have no Chat form. Content that already sits in session history (a PDF read earlier, a hosted search result) must not break every later request; a tool the client asks the provider to run (hosted web search) must not silently return invented results.

## Decision

**Adopt** an allowlist build of the Chat body. Optimization and metadata fields with no Chat meaning are dropped silently, and fields with privacy or compatibility cost (`metadata.user_id`, `reasoning_effort`) are off unless the operator opts in per provider.

Unsupported content **blocks** in messages are replaced by a one-line text placeholder naming the omitted block type, so the session continues and the model can tell the user. Unsupported typed server **tool definitions** in `tools` are rejected with HTTP 400 naming the tool, so the client or model sees the failure. Both lists are normative in the [request spec](../spec/01-request-mapping.md).

## Consequences

- One unsupported block never poisons a session.
- Built-in web search fails loudly on a bridged provider.
- The model sees a placeholder where a document stood and cannot read it; this is lossy by design.
- Adding a field to either list is a spec change, not an implementation detail.
