# Prompt caching with dynamic agent tools

Each harness-to-LLM step is a separate HTTP request, including tool-call continuations of one human
message. Upstream caching compares reusable prompt prefixes across those requests, not logical chats.
Changing tool definitions, availability or order can invalidate the prefix after the change.

## What the router does

Caching injection is enabled by a matched model mapping's `Caching: true`; unmatched passthrough does
not acquire cache controls from the router.

| Upstream | Router behavior | Limits |
|---|---|---|
| Anthropic Messages | On requests without caller cache controls, mark the last tool definition, last system block and last message content block with ephemeral cache controls. String system/message content becomes a text-block array. | The cache hierarchy is **tools → system → messages**. Tool changes invalidate the later system and message cache. |
| OpenAI Responses | Set `prompt_cache_key` to the resolved session identity when session forwarding is opted in and has an identity; otherwise use the inbound model. | The key influences cache routing; it does not replace matching-prefix requirements or guarantee reuse. |
| OpenAI Chat Completions, including Responses→Chat downgrades | No router-injected `prompt_cache_key` or Anthropic cache controls. | Caching behavior belongs to the selected upstream. An OpenAI-compatible API does not imply OpenAI caching support. |

Anthropic caller-managed caching takes precedence: existing explicit tool/system/message cache controls
or a top-level automatic `cache_control` are left intact, without additional router breakpoints. This
preserves the caller's TTL ordering and avoids adding to its four-explicit-breakpoint budget. A tools
breakpoint can cache the tools-only prefix when later content changes; it cannot protect changed tools
or guarantee that appending a tool reuses an earlier tools breakpoint.

## Let the conversation grow

- Keep the tools supplied by the harness stable and in a deterministic order while they are usable.
  When the harness supports it, keep core tools first and append newly available tools at the end.
  This minimizes prefix changes; it does **not** guarantee preservation of the conversation cache.
- Add or remove tools when the task requires it. Do not advertise a removed or unavailable tool merely
  to preserve caching. By default the router does not reorder tools; it never resurrects definitions.
- After a tool change, expect a cache write/miss for the affected prefix. Later requests with a matching
  prefix **can** reuse it within the upstream's lifetime and minimum-length rules. Repeated tool churn
  can repeatedly invalidate it; “one miss, then hits” is an expectation, not a guarantee.
- A new chat only changes the router's Responses cache key if it supplies a different resolved session
  identity. Without one, the inbound-model key is shared. Anthropic has no router session cache key;
  identical prefixes can be reused subject to provider cache isolation and lifetime rules.
- HLD 004 normalization can drop invalid tools, and the optional HLD 014 decision filter can change the
  kept set as a chat advances. `AlwaysKeep` protects supplied core tools and `MinKeepTools` bounds pruning;
  neither freezes a session catalog. The per-question decision memo saves classifier work, not upstream
  prompt-cache tokens. Deferred-loading catalogs are not decision-filtered. If stable upstream prefixes
  are the priority, leave `DECISIONS_TOOLS_ENABLED=false` (the default), or also set
  `DECISIONS_TOOLS_STICKY_PER_SESSION=true`: a tool the filter forwarded then stays forwarded for the rest of
  that session while the client still sends it, so the filter itself no longer changes the tool prefix between
  turns. It uses the same captured identities as ordering below (derived fingerprints and the router's own
  `prompt_cache_key` do not count), records nothing from a failed or all-skip pass, and grows the forwarded
  catalog over a long session. Combined with ordering, a session's tool prefix changes only when the client
  adds or drops a tool ([HLD 014 LADR-09](../hlds/014-decisions-tools-filter/ladrs/LADR-09-sticky-per-session.md)).

## Optional session-local stable ordering

Set `Imposter__ToolOrdering__Enabled=true` and restart the router to opt completion requests into
first-seen tool ordering. No shipped configuration enables it. It works independently of provider
session forwarding and caching, on imposter and passthrough Chat, Responses and Messages routes.

The first eligible catalog establishes the order. On later requests, previously seen tools keep their
relative ranks and newly seen tools append in the current request's order. Removed tools remain removed;
reintroduced tools recover their earlier rank. Definitions always come from the current request, so a
description/schema change is forwarded rather than replaced with a stale copy. Names, choices and tool
history are not rewritten. The Anthropic injector runs afterward so its tool breakpoint marks the final
catalog tail.

Ordering requires a captured identity: `session_id`, `x-opencode-session`, `x-session-id` or
`conversation_id` header, then body `prompt_cache_key` or `metadata.user_id`. Use an actual per-chat
identifier, not a shared model-name cache key. A derived caller fingerprint or absent identity does
not enable ordering. Scopes also include dialect, inbound model and standard caller auth/identity headers.

Only ordinary named function/custom tools (or Anthropic name/input-schema tools) are ordered. Unknown,
duplicate, hosted or namespace catalogs, deferred tool loading, Responses-managed state, and
caller-managed cache layouts are left in their supplied order. State is bounded to 256 scopes and 1,024
tool identities per scope, expires after 30 minutes idle, and is lost on restart or eviction. At a scope's
tool-rank limit the request keeps its supplied order. A different replica may establish a different order.
Rank-limit requests still count as scope activity. Responses explicit text-block
`prompt_cache_breakpoint` controls preserve caller order even without `prompt_cache_options`.
The memory stores hashed scope/tool keys and ranks, not credentials, raw session IDs or tool bodies.

This removes avoidable **reorder-only** churn. Actual additions, removals or definition changes still
change the rendered provider prefix, so later system/conversation caching can still be invalidated.

## Verify actual reuse

Compare upstream usage for an unchanged catalog, a changed catalog, and a repeat of the changed catalog.
Keep model and relevant settings stable and use a prompt long enough for the provider's cache. For
Anthropic, inspect `cache_creation_input_tokens` and `cache_read_input_tokens` (also in streamed
`message_start` usage). For OpenAI Responses inspect `usage.input_tokens_details.cached_tokens`; for
Chat Completions inspect `usage.prompt_tokens_details.cached_tokens`, when the upstream provides them.
The router preserves the ordinary upstream response; tests against a local stub verify request shaping,
not a provider cache hit.

Sources: [Anthropic prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)
and [OpenAI prompt caching](https://developers.openai.com/api/docs/guides/prompt-caching).
