# LADR-10: Codex `additional_tools` requests are not filtered

Status: Accepted (2026-10-24)

## Context

A captured Codex `/v1/responses` body (model `gpt-6.1-sol`) has no top-level `tools`. Its tools arrive as
`input[0]`, an `additional_tools` item (`role: developer`) holding 4 namespaces and 12 leaf tools, with no
`tool_search`, `defer_loading`, `previous_response_id` or `conversation`. The filter reads only top-level
`tools`, so it forwarded the body untouched and, until now, logged nothing.

## Decision

Leave Codex requests unfiltered. Do not read `additional_tools`.

- The catalog is small (12 tools, 4 namespace candidates), below the `MinKeepTools` floor, so a pass would
  skip anyway.
- `additional_tools` is the first prompt item; changing it invalidates the cache for everything after it.
- `exec` is the core Codex tool; removing a namespace could strand the others.

Every skip is logged at Information with its reason, so the cause of an unfiltered request is visible
without a body capture.

## Revisit when

A Codex or Responses client sends a catalog well above the floor outside top-level `tools`.
