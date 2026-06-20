# LADR-03: Classify every Responses Item and field before downgrade

**Status:** Draft

## Context

OpenAI's migration guide describes Responses as an Item-oriented API: messages, reasoning,
function calls, and function-call outputs are separate typed Items. Chat Completions accepts a
message transcript instead. The current downgrade handles common message and function Items but can
accidentally turn unknown Items into empty user messages because it treats any non-function input item
as message-like.

Responses also has state fields, such as `previous_response_id`, that have no stateless Chat
Completions equivalent. Silently dropping those fields can make a request appear successful while
removing the context the client expected the model to use.

## Decision

**Classify** every Responses request construct that can reach the `/responses`→Chat downgrade as one
of four policies: preserve, convert, remove, or reject.

Message Items and simple string input are converted to Chat messages. Paired `function_call` and
`function_call_output` Items are converted according to LADR-01. Incomplete function history is
removed according to LADR-02. Responses-only Items such as `reasoning` or hosted-tool outputs are not
converted into user messages; they are removed when loss is acceptable, or rejected when the request
depends on them for correctness.

Two classifications are pinned explicitly so they cannot regress by incidental fallthrough:

- **Hosted-tool Items are removed only when their type carries the `_call`/`_call_output` suffix**
  (e.g. `web_search_call`, `code_interpreter_call`, `mcp_call`). Hosted Items **without** that suffix —
  `mcp_list_tools`, `mcp_approval_request`, and similar — are **rejected** like unknown Item types, not
  silently dropped. A non-suffixed hosted Item can carry correctness-relevant intent (an approval the
  model issued, a tool listing it relied on), so fail-fast is the safe default until a live eval shows a
  specific type is safe to drop. Broadening removal to a named hosted-Item set is the documented escape
  hatch if that evidence appears.
- **A `function_call_output.output` sent as a structured content array** (rather than a plain string) is
  **JSON-stringified** into the Chat `tool` message `content`, because Chat Completions tool content must
  be a string. The structure is preserved as JSON text rather than reduced to its text part(s), so no
  tool-result content is lost on the wire.

State pointers such as `previous_response_id` are rejected on the downgrade path. Clients that need
Chat-compatible routing must omit the state pointer and replay the needed Items in `input`. The proxy
is stateless and must not pretend it can retrieve Responses state from an upstream Chat provider.

## Alternatives Considered

- **Best-effort convert unknown Items into text messages** — rejected: it fabricates roles/content and
  can change model-visible meaning.
- **Silently drop every unsupported field** — rejected: state loss is hard to diagnose and can produce
  plausible but context-free answers.
- **Fetch prior Responses state** — rejected: the router is stateless and the upstream is Chat-only.

## Consequences

- The downgrade gains an explicit compatibility matrix instead of relying on default fallthrough.
- Some requests fail earlier with a dialect-shaped error instead of being sent upstream without needed
  context.
- Future Responses Item types require a conscious policy decision before they can be downgraded.

## Related

- **LADR-01** — paired tool Items are one classified conversion.
- **LADR-02** — incomplete tool Items are classified as removal.
- **HLD 004 LADR-05** — response translation stays separate from request Item policy.
