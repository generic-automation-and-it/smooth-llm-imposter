# LADR-04: Convert compatible Structured Outputs shape

**Status:** Accepted

## Context

OpenAI's migration guide states that Structured Outputs use `response_format` in Chat Completions
and `text.format` in Responses. The existing downgrade copies `response_format` when it is already
present, but a Responses-mode client can legitimately send `text.format`. A Chat-only upstream will
not understand that Responses-shaped field.

## Decision

**Convert** compatible Responses `text.format` Structured Outputs into Chat Completions
`response_format` on the `/responses`→Chat downgrade path.

For JSON schema formats that have a faithful Chat equivalent, the downgrade maps the schema into the
Chat `response_format` shape expected by OpenAI-compatible Chat upstreams. Unsupported text formats
are rejected rather than silently removed, because output formatting is a caller-visible contract.

This conversion is request-only. It does not change the response bridge's responsibility to emit
Responses-shaped output events or objects back to the caller.

## Alternatives Considered

- **Ignore `text.format`** — rejected: the caller asked for a structured output contract that the Chat
  upstream would never see.
- **Drop unsupported formats** — rejected: the model could return unstructured text while the caller
  expects structured JSON.
- **Treat all `text` options as equivalent to Chat** — rejected: only documented compatible shapes
  should be converted.

## Consequences

- Structured-output requests can work against compatible Chat upstreams when the schema shape is
  representable.
- Unsupported formats fail fast with a clear error.
- The request transformer must check both `response_format` and `text.format` precedence on the
  downgrade path.

## Related

- **LADR-03** — Structured Outputs are part of the explicit field policy.
- **HLD 004 LADR-02** — request-only conversion remains the boundary.
