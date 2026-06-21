# Routing — Feature Context

## TL;DR

Stateless same-dialect LLM router: reads the inbound `model`, rewrites it to a configured upstream
("imposter") or passes through to the dialect's default provider, optionally injecting prompt caching,
and streams the response back. Design rationale lives in `.docs/hld/001-llm-imposter-routing/`
(`README.md` index → `diagrams/`, `nfrs/`, `ladrs/` subfolders).

## Non-Negotiables

- **Never persist, log, or echo provider `Secret` values.** They live only in `ImposterOptions` (config/env). Logs
  carry provider name + model names only.
- **Transparent proxy — do not strip or rewrite the request.** The forwarder relays the caller's inbound
  headers and body to the upstream **unchanged**, with exactly three sanctioned request-rewrite classes: (1) the
  **auth** header is managed (see below), (2) **caching injection** rewrites the body on a matched imposter
  route, and (3) **opt-in request normalization** reshapes the body on a matched OpenAI imposter route that
  opted in (HLD 004 — see "Request normalization" below). These three are **request-only**. The single sanctioned
  response rewrite is `ChatToResponsesStreamTransformer`, and only on the matched OpenAI imposter
  `/responses`→Chat downgrade path (`OpenAiUpstreamApi: chat_completions` + inbound `/responses`); it is an
  incremental SSE transform, never a buffer/replay step (HLD 004 LADR-05 / NFR-05). Every other response stream is
  byte-relayed unchanged (HLD 001 LADR-003 as narrowed by LADR-05). Adding a
  bespoke filter that drops a caller header (e.g. `anthropic-beta`) breaks beta body fields like
  `context_management` — only the fixed hop-by-hop/content set (`Host`, `Content-*`, `Connection`,
  `Transfer-Encoding`, `Accept-Encoding`, …) is withheld. The caller's own `anthropic-version` passes through;
  the default `2023-06-01` is supplied **only** when the caller omitted it. The **one** caller header dropped
  beyond that fixed set is `chatgpt-account-id`, and only when auth is **managed** (a provider/override secret is
  applied): it asserts a ChatGPT *identity* that an OpenAI-compatible gateway (opencode) honours over the managed
  Bearer key and 401s on, so it belongs to the managed-auth concern. It is **kept on key-less passthrough**, where
  the caller's own credential + identity are a matched pair (`ManagedAuthIdentityHeaders` in the forwarder).
- **Auth header is the one managed header, route-dependent:** a matched imposter route sends the provider's
  configured `Secret` (never the caller's). On **passthrough**: the HLD-003 override ON ⇒ active stored Bearer; else
  a configured provider `Secret` / stored credential if present; else the caller's own `Authorization`/`x-api-key`
  is forwarded verbatim, so a key-less router authenticates with the caller's credential.
- **Auth *scheme* is decoupled from `Dialect`.** Provider config carries `Secret` + optional `AuthScheme`
  (`Bearer` → `Authorization: Bearer`, `ApiKey` → `x-api-key`). The scheme precedence
  (`credentialOverride.AuthScheme ?? provider.AuthScheme ?? dialect default`, openai → Bearer, anthropic → ApiKey;
  the HLD-003 override forces Bearer) lives in **one place**: `Domain.Routing.UpstreamAuthResolver`. Both the
  forwarder (which writes the header) and `ImposterRouter` (which logs `auth=`) call it — keep them on the shared
  resolver so the log can't drift from the wire behavior. So an `openai`-dialect upstream (e.g. opencode) can
  authenticate with `x-api-key` via `AuthScheme: ApiKey` without changing its wire dialect. There is no `ApiKey`
  config alias — `Secret`/`AuthScheme` is a breaking rename.
- **`AuthScheme` is inert without a `Secret`, and imposter routes never borrow the caller's credential.** A
  matched imposter route authenticates **only** with the provider's configured `Secret`; if that is empty, the
  forwarder sends **no** auth header at all (the caller's `Authorization`/`x-api-key` is forwarded only on
  passthrough), so the upstream 401s. `AuthScheme` merely picks the header for a *non-empty* secret. The routing
  log surfaces this as `auth=none` (imposter, no secret), `auth=Bearer`/`auth=ApiKey` (secret present), or
  `auth=caller-passthrough` (caller's own credential relayed) — `auth=none` on a 401 means a missing `Secret`.
- **Same-dialect only.** Do not add OpenAI⇄Anthropic body translation here. An `openai` provider serves
  openai requests; an `anthropic` provider serves anthropic requests.
- **OpenAI Responses→Chat compatibility is explicit per provider.** `OpenAiUpstreamApi` defaults to
  `responses`. Set `OpenAiUpstreamApi: chat_completions` only for OpenAI-compatible upstreams that lack
  `/responses` (e.g. OpenRouter/opencode). On matched imposter routes only, `/responses` is forwarded to
  `/v1/chat/completions` and common Responses `input`/`instructions` payloads are converted to Chat
  Completions `messages`. The conversion also **folds `role:"developer"` → `role:"system"`**: Moonshot/kimi
  (and some OpenAI-compatible Chat upstreams) reject the OpenAI `developer` role with "tokenization failed",
  and `developer` is OpenAI's successor to `system`. Real `/responses` upstreams keep `developer` (the
  conversion only runs for `chat_completions`). The response side is paired: Chat Completions SSE is translated
  back to Responses SSE on that same downgraded path so Responses clients can keep `wire_api = "responses"`.
  Passthrough/default routes and direct `/chat/completions` callers stay transparent.
- **Request normalization is OpenAI-imposter-only, request-only, and ON by default for `chat_completions`
  (HLD 004).** A provider's `RequestNormalization` (`CodexToOpenAiSdk` / `None`) selects a normalizer that
  mutates the parsed request body in `OpenAiRequestTransformer` **before** the Responses→Chat conversion. The
  effective profile is resolved in `ProviderCatalog`: `OpenAiUpstreamApi: chat_completions` **defaults it on**
  (`CodexToOpenAiSdk`) unless config sets it explicitly (including to `none` to opt out); a `responses` upstream
  defaults it **off** and must never enable it (the validator rejects an explicit `codex_to_openai_sdk` outside
  `chat_completions`/`openai`, because those tool types/names are valid on `/responses`). It runs **only** when
  `decision.IsImposter` **and** a normalizer matches the resolved profile — so passthrough/default routes and
  `None` providers are byte-transparent. Normalizers are **request-only** (never read/rewrite the response) and
  must be **idempotent**; they prefer **removing** an offending element over remapping it, so the transform stays
  one-directional (HLD 004 LADR-02). The default-on policy **amends HLD 004 LADR-03** (originally per-provider
  opt-in, off by default): normalization targets the *generic* OpenAI Chat Completions tool contract — any
  `chat_completions` upstream (opencode, openrouter, Bedrock, …) rejects the same Responses-dialect catalog — and
  it is a no-op for clean clients, so default-on is the correct safe default for `chat_completions`. This also
  supersedes the HLD 001 LADR-006 "no in-proxy tool-name sanitization" stance for OpenAI imposter routes (HLD 004
  LADR-01).
- **`BaseUrl` is the server root WITHOUT a version path** (`https://api.openai.com`, not `.../v1`). The
  upstream request path is appended verbatim; adding `/v1` to config double-prefixes the path. The `/v1`
  belongs in exactly one place — the caller's path or the provider `BaseUrl`, never both. (E.g. OpenRouter
  nests its OpenAI surface under `/api/v1`, so its `BaseUrl` is `https://openrouter.ai/api`.) For dialect-
  prefixed inbound routes the Host strips the `/openai` or `/anthropic` prefix first, so the forwarder still
  receives a clean upstream path (`/v1/...`).
- **Do not add a standard resilience handler to the `imposter-upstream` client.** SSE streams outlive its
  timeouts and a half-streamed POST can't be replayed; the client uses an infinite timeout bounded by the
  caller's `CancellationToken` (see HLD LADR-003).
- **All body work stays string-in/string-out in Application; HTTP I/O stays in Host.** Infrastructure is
  `System.Net.Http` only — don't leak `HttpContext` into Application/Infrastructure.
- **No Mediator / FluentValidation request pipeline here** (opaque proxy bodies). Validation is on
  configuration at startup (`ImposterOptionsValidator`), not on requests (HLD LADR-001).
- **Provider configuration is runtime-mutable (HLD 008 Phase 1).** The startup config/env baseline seeds
  `IProviderRegistry` once; after that `/admin/providers` is authoritative until restart. The routing catalog,
  resolver, and local model responders are scoped and consume `IOptionsSnapshot<ImposterOptions>`, whose final
  post-configure overlays the registry. Do not reintroduce singleton catalog/resolver capture of options.
- **Provider-config CRUD is secret-free.** `/admin/providers` can list/get/upsert/delete/enable/disable routing
  config, but neither accepts nor returns `Secret`. A provider-config `PUT` preserves the existing secret for
  that provider key; new runtime providers start with no secret until the credential boundary owns one.

## Key Behaviors

- **First match wins, in configuration order.** The resolver scans the dialect's providers top-to-bottom and
  returns the first `Models[].From` that matches; order providers/mappings from most to least specific.
- **`From` matching** is exact or single trailing-`*` wildcard (`claude-haiku-*`), case-insensitive (`ModelMatcher`).
- **No match → default passthrough** (model unchanged, no caching) via the dialect's `IsDefault` provider.
  No match **and** no default → `RoutingException(404)`. At most one `IsDefault` per dialect (startup-validated).
  The shipped `appsettings.json` declares **catch-all key-less defaults** for `anthropic` (`api.anthropic.com`)
  and `openai` (`api.openai.com`); remove them for type-only impostering (404 on unmatched; HLD LADR-005).
- **Disabled providers are retained but invisible to resolution.** `ProviderOptions.Enabled` defaults `true`.
  When `false`, the provider is skipped for imposter model matching and for default/passthrough selection; its
  config (including model mappings and default flag) remains in the runtime registry so re-enabling restores it.
  The "at most one default" validation counts enabled providers, so a disabled default can coexist with another
  enabled default, but re-enabling into a duplicate default is rejected by the admin mutation validation.
- **Caching is per-dialect** (only when `Caching: true`): Anthropic injects ephemeral `cache_control` on the
  `system` block (a string `system` is converted to a one-element block array) and on the last content block
  of the last message; OpenAI sets `prompt_cache_key` to the **inbound** model name.
- **Request normalization — `CodexToOpenAiSdk` v1** (`Features/Routing/Normalization/`). The seam is a
  `IReadOnlyDictionary<RequestNormalization, IRequestNormalizer>` in `OpenAiRequestTransformer`; adding a profile
  is a new `IRequestNormalizer` + enum value, not a router/forwarder branch. v1 keeps only upstream-valid
  `function` tools: drops tools whose `type` ∉ {`function`,`plugin`} (`custom`/`web_search`/`image_generation`/
  `tool_search`/…), **flattens** `type:"namespace"` wrappers into their nested `function` tools (so the Codex
  GitHub connector's `_`-prefixed tools survive), drops `function` names that fail `^[A-Za-z_][A-Za-z0-9_-]*$`
  (empty/dotted/leading-digit), and cleans any `tool_choice` referencing a removed tool. Handles **both** tool
  shapes (flat Responses `{type,name}` and nested Chat `{type:"function",function:{name}}`) because it runs
  before `ToChatCompletions`/`ConvertTools` — flattened flat-function tools are then nested by `ConvertTools` for
  chat upstreams and stay flat for responses upstreams. If no tool survives, `tools`+`tool_choice` are removed
  (absent tools are accepted; an empty array is not guaranteed to be). Prior-turn `function_call`/
  `function_call_output` history for a dropped tool is **left untouched** — v1 only filters `tools[]`.
- **Response translation — `ChatToResponsesStreamTransformer`.** This is the only response-side transformer. It
  consumes upstream Chat Completions SSE line-by-line and emits Responses SSE frames as each source frame arrives:
  `response.created`/`in_progress`, message/content-part open events, text/reasoning/tool-call deltas, done
  events, then exactly one `response.completed` with assembled output + usage. It carries only bounded per-stream
  state (current message/content part, accumulated text, per-index function-call arguments, ids, usage) and is
  gated by the exact `/responses`→`/v1/chat/completions` downgrade predicate. Non-streaming Chat Completion
  objects are mapped to a Responses object on the same path. Off-path responses use the byte-copy loop.
- **Responses input-history downgrade — HLD 006.** On the matched OpenAI imposter `/responses`→Chat path,
  `OpenAiRequestTransformer` now classifies Responses input Items before creating Chat `messages`: paired
  `function_call`/`function_call_output` Items are emitted as adjacent assistant/tool messages, incomplete tool
  history is removed, a message Item that converts to **empty** content (null, empty, or built only from content
  parts with no Chat representation — e.g. an empty `output_text` or refusal-only assistant turn beside a
  `function_call`) is **dropped** rather than emitted as a Chat message with neither content nor `tool_calls`
  (strict upstreams 400: `message at position N with role 'assistant' must not be empty`), `reasoning` and
  hosted-tool **call** Items (type ending `_call`/`_call_output`, e.g.
  `web_search_call`, `mcp_call`) are explicitly removed, while hosted Items **without** that suffix
  (`mcp_list_tools`, `mcp_approval_request`) and unknown Item types **fail fast** (LADR-03 reject — a non-suffixed
  hosted Item may carry correctness-relevant intent), and the state pointers `previous_response_id` **and**
  `conversation` (the Conversations API pointer) are both rejected (400, present-and-non-null guarded) because the
  router cannot resolve Responses-managed state for a stateless Chat upstream. A structured `function_call_output.output`
  array is JSON-stringified into the Chat `tool` message `content` (Chat tool content must be a string). Compatible
  Responses `text.format` Structured Outputs are converted to Chat `response_format`; unsupported formats fail
  before the upstream is called. Request-field fidelity (LADR-03): `reasoning.effort` is converted to Chat top-level
  `reasoning_effort` for compatible values (`minimal`/`low`/`medium`/`high`) and **dropped** for `none`/unknown
  (`none` disables GPT-5.4+ tool calling on Chat, and this path is tool-heavy); the Chat-shared generation knobs
  `stop`/`metadata`/`logit_bias`/`logprobs`/`top_logprobs` pass through as **named** allowlist additions (never a
  blanket copy — Responses-only fields would 400 a Chat upstream). The `ToChatCompletions` copy set is an allowlist:
  anything not explicitly copied (or converted) is dropped, so each state/behavior field gets a deliberate policy.
- **Errors are dialect-shaped**: OpenAI `{error:{message,type}}`, Anthropic `{type:"error",error:{type,message}}`.
  Routing failures → 400/404; upstream transport failures → 502.
- **`anthropic-version`**: the caller's value is forwarded as-is; `2023-06-01` (or a configured
  `AnthropicVersion`) is supplied only when the caller omitted the header.
- **Header forwarding** is driven by `CallerHeaders` (the full inbound header set, captured at the Host edge in
  `RoutingEndpoints` so `HttpContext` never leaks downstream). `UpstreamForwarder.ForwardCallerHeaders` copies
  every header except the fixed `NonForwardableHeaders` set (hop-by-hop, content, and the auth headers, which
  `ApplyAuthentication` owns). This is what lets vendor `x-*` and `anthropic-beta` headers reach the upstream.
- **Dialect-prefixed endpoints** are the primary contract: clients call `/openai/{**path}` or
  `/anthropic/{**path}` (any HTTP method). `RoutingEndpoints` derives the dialect from the prefix, strips it, and
  forwards `{path}` verbatim with the **inbound method** — so `/v1/models`, `/v1/responses`,
  `/v1/messages/count_tokens`, etc. proxy with no per-endpoint mapping. The prefix is what disambiguates shared
  paths like `/v1/models` (byte-identical across both dialects). Legacy unprefixed `POST /v1/chat/completions`,
  `/v1/responses`, `/v1/messages` stay mapped; unprefixed `/v1/models` is intentionally **not** mapped (ambiguous).
- **Body-less requests passthrough to the default.** A request with no JSON body (e.g. `GET /v1/models`) has no
  model to resolve, so `ImposterRouter.PlanPassthroughAsync` → `IRouteResolver.ResolveDefault` routes it to the
  dialect's `IsDefault` provider with no transform and no caching; the forwarder issues it with the inbound
  method and **no content**. No default for the dialect ⇒ 404. An imposter route is never selected for a body-less
  request — imposter matching keys off the body's `model`, which a discovery probe doesn't carry. This passes
  through the same credential seam (stored credential / HLD-003 force-Bearer / fail-closed 403).
- **`GET /openai/v1/models` is answered locally, not passthrough (HLD 005).** The Host registers a specific
  `MapGet("/openai/v1/models", …)` that outranks the `/openai/{**upstreamPath}` catch-all for GET, so it
  short-circuits the discovery probe to a synthesized OpenAI `ListModelsResponse` built from the route
  catalogue alone — the **distinct union of every `to`** across the OpenAI-dialect providers, with the
  first declaring provider supplying `owned_by` on a duplicate. `IModelCatalogResponder` lives in Application
  and is string-out (no `HttpContext`, no upstream, no credential seam — NFR-03/04). Default/passthrough
  providers carry no `Models[]` and contribute nothing. Scope is narrow: non-GET on the same path still
  passthrough (LADR-03). `created` is a fixed constant (`0`), so two calls under one config are byte-identical (NFR-01).
- **`GET /anthropic/v1/models` is answered LOCALLY, not forwarded (HLD 005, Anthropic scope).** The Anthropic
  twin of the OpenAI behavior above: a `GET` whose dialect is Anthropic and whose post-prefix path is
  `/v1/models` is recognized in the Host (`MapGet("/anthropic/v1/models")`, which wins over the
  `/anthropic/{**path}` catch-all for GET) and served from `IAnthropicModelCatalogResponder` — the **distinct**
  (ordinal, catalogue-order) union of every Anthropic `to` target, as a valid Anthropic List Models body
  (`{data, first_id, has_more, last_id}`; each `ModelInfo` is minimal — `id`, `type:"model"`,
  `display_name` (= the `to` id verbatim), `created_at` fixed `1970-01-01T00:00:00Z`). The Anthropic envelope
  differs from OpenAI's: no `object:"list"` field, `type:"model"` not `object`, RFC3339 `created_at` not integer
  `created`, `display_name` not `owned_by`. No upstream call, no credential read, no DB (NFR-03); never serializes
  a `Secret` (NFR-04); the constant `created_at` ⇒ byte-identical responses (NFR-01). Aggregation is string-out in
  Application; recognition is in Host (LADR-04). Scope is exactly that one case: a **non-GET** on
  `/anthropic/v1/models`, and every other path, stay transparent passthrough (LADR-03).
- **Forwarder method/body**: `UpstreamForwarder.SendAsync` takes the inbound `HttpMethod` and a nullable body;
  `Content` is attached only when the body is non-empty. GET probes therefore reach the upstream as real GETs.
- **Mid-stream caller disconnect is swallowed, not retried.** `RoutingEndpoints.HandleAsync` wraps the streaming
  copy so that when the caller aborts mid-stream (`context.RequestAborted` fires — common with SSE clients), the
  resulting `OperationCanceledException`/`IOException` is caught and the handler returns quietly. The catch is
  gated on `cancellationToken.IsCancellationRequested`, so a genuine streaming failure while the caller is still
  connected still propagates and is logged. The status line + partial SSE are already on the wire, so there is
  nothing to write and (per LADR-003) nothing to retry. This mirrors the existing forward-path guard.
- **Tool function names are never renamed.** On `chat_completions` imposter routes, HLD 004 request normalization
  may drop upstream-invalid tool definitions or flatten namespace wrappers, but it does not invent alternate names
  and it does not rewrite prior-turn tool history. This preserves Codex's dispatch contract while avoiding strict
  upstream 400s for unsupported tool shapes/names. The LADR-05 response bridge is a **wire-shape** translation for
  downgraded `/responses` calls, not a tool-name remapper.

## Credential Overrides

- **HLD 002 — credential persistence & overrides** (`.docs/hld/002-credential-persistence-overrides/`, status
  *Accepted*) reintroduced EF Core + PostgreSQL for stored **passthrough** credentials and added the
  Mediator-based `/admin/credentials` API. Routing has exactly **one credential seam**: after no-match →
  default/passthrough resolution, `ImposterRouter` consults `ICredentialStore` for the active dialect credential
  and passes a decrypted `RouteCredentialOverride` to the forwarder. **Do not** extend this to matched-imposter
  routes — those stay config-key-only and DB-free (HLD 002 LADR-004). The hot-path non-negotiables above are
  unchanged; the admin API uses Mediator/FluentValidation while routing stays raw (HLD 002 LADR-005).
- **Persistence is opt-in.** `AddInfrastructure` wires EF Core + `CredentialStore` **only** when
  `ConnectionStrings:ImposterDb` is set; otherwise it registers a `NullCredentialStore`. This keeps the
  stateless/key-less default booting with **no database**: the passthrough seam resolves a `null` credential
  (then forwards caller auth, above) instead of opening a connection. The credential-admin and authorization-
  override features simply require a connection string to be available.

## Authorization Override (HLD 003)

- **`IAuthorizationOverrideSwitch`** (`Features/AuthorizationOverride/`, in-memory singleton, default OFF) is read
  on **exactly one line** — `ResolvePassthroughCredentialAsync`, the same seam above. When ON for a dialect, the
  returned `RouteCredentialOverride` carries **`ForceBearer = true`**, and the forwarder presents the active
  credential's secret as `Authorization: Bearer` while omitting `x-api-key`, regardless of the stored `AuthScheme`.
  Because the imposter branch returns `null` before this method, it never reads the switch or the store (LADR-003) —
  a throwing-spy unit test enforces this.
- **Fail closed:** override ON + no active credential ⇒ `RoutingException(statusCode: 403)`, surfaced as a
  dialect-shaped `permission_error` (`RoutingEndpoints.ErrorTypeFor`). Never falls back to `x-api-key`/config key
  (LADR-005). Arm-time refusal (no active credential at `PUT`) is handled in the Mediator slice, not here.
- The switch adds **no** DB read of its own — it gates HLD 002's existing active-credential lookup (NFR-003).
  See `Features/AuthorizationOverride/AUTHORIZATION_OVERRIDE_AGENTS.md` for the toggle slices and endpoint contract.

## Test References

- **L0** `Domain.UnitTest/Routing` — matcher, dialect parser.
- **L0** `Application.UnitTest/Routing` — resolver, transformers (cache injection), router, error factory, options
  validator, `CodexToOpenAiSdkNormalizerTests` (normalization drop/flatten/name-rules/tool_choice/idempotency, flat+nested shapes), model-catalog responder.
- **L3** `Upstream.EvalTest` — **live** opencode-go conformance eval (HLD 004): a raw Codex catalog run through the
  real transformer+normalizer is accepted (200), un-normalized is rejected (400). Excluded from `SmoothLlmImposter.slnx`;
  secret-gated on `OPENCODE_API_KEY`, neutral (skipped) when absent; runs only in `pr-evals-gate.yml`.
- **L2** `Host.IntegrationTest` — full pipeline incl. SSE passthrough, mid-stream caller-disconnect handling
  (`StreamingDisconnectTests`), and env-over-appsettings override (in-process stub upstream). The disconnect test
  asserts on the process-global Serilog `Log.Logger` (where request-logging surfaces the escaping exception), so
  the integration suite runs serially (`DisableTestParallelization` in `GlobalUsings.cs`).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-14 | Initial routing feature: same-dialect router, config-driven imposters, per-dialect caching, SSE streaming. | — |
| 2026-06-14 | Moved full LADRs + C4/flow/sequence diagrams to HLD 001; trimmed this file to minimal AI-coder context. | — |
| 2026-06-15 | HLD 001 split into `README.md` index + `diagrams/`, `nfrs/`, `ladrs/` subfolders. | — |
| 2026-06-15 | Default config: removed `IsDefault` providers (type-only impostering, 404 on unmatched; LADR-005). New providers opencode-go/openrouter/opencode-anthropic. | — |
| 2026-06-15 | Implemented HLD 002 passthrough credential override seam; matched imposter routes remain config-key-only and DB-free. | HLD 002 |
| 2026-06-17 | Implemented HLD 003 passthrough authorization override: in-memory per-dialect force-Bearer switch read only on the passthrough seam, fail-closed 403 (`permission_error`), imposter path untouched. | HLD 003 |
| 2026-06-17 | Renamed provider config key `Api` → `Dialect` (`ImposterOptions.ProviderOptions.Dialect`) to match the `ApiDialect` ubiquitous language; breaking config change — `Imposter__Providers__N__Api` is no longer bound. | — |
| 2026-06-17 | Forwarder is now a transparent proxy: relays the caller's full inbound header set (`CallerHeaders`) verbatim minus a fixed hop-by-hop/content/auth set — so `anthropic-beta` (and the matching `context_management` body field), vendor `x-*`, and the caller's `anthropic-version` reach the upstream. Only the auth header is managed: key-less passthrough forwards the caller's own `Authorization`/`x-api-key`; imposter routes use the provider key; HLD-003 override forces the active stored Bearer. | — |
| 2026-06-17 | Persistence is opt-in: `AddInfrastructure` registers a `NullCredentialStore` when `ConnectionStrings:ImposterDb` is unset, so the stateless default boots without PostgreSQL. Fixed EF discriminator NRE (shadow column `ProviderDialect` → `Dialect`) that crashed model build on the passthrough path. | HLD 002 |
| 2026-06-19 | Added dialect-prefixed routing (`/openai/{**path}`, `/anthropic/{**path}`, any method): prefix selects dialect, tail forwarded verbatim — disambiguates shared paths like `/v1/models`. Body-less requests (`GET /v1/models`) passthrough to the dialect default via `PlanPassthroughAsync`/`ResolveDefault` (no model to match). Forwarder now forwards the inbound `HttpMethod` with a nullable body. Legacy unprefixed `POST /v1/*` retained; unprefixed `/v1/models` left unmapped (ambiguous). | — |
| 2026-06-20 | Added `OpenAiUpstreamApi: chat_completions` for OpenAI-compatible upstreams without `/responses`; matched OpenAI imposter routes can downgrade `/responses` requests to `/v1/chat/completions` and convert common Responses payload fields to chat `messages`. | — |
| 2026-06-20 | Renamed provider config `ApiKey` → `Secret` and added `AuthScheme` (`Bearer`/`ApiKey`), decoupling auth scheme from `Dialect`. Forwarder resolves `override.AuthScheme ?? provider.AuthScheme ?? dialect default` (openai → Bearer, anthropic → ApiKey) via a single unified path; this fixes openai-dialect upstreams (opencode) that require `x-api-key`. Breaking config change — no `ApiKey` alias. | — |
| 2026-06-20 | Mid-stream caller disconnect on the streaming path is now caught in `RoutingEndpoints.HandleAsync` (`OperationCanceledException`/`IOException` gated on `RequestAborted`) and returns quietly, instead of bubbling an unhandled exception logged at Error by Serilog request logging. Genuine non-abort streaming failures still propagate. Added `StreamingDisconnectTests`. | #17 |
| 2026-06-20 | Extracted auth-scheme precedence into `Domain.Routing.UpstreamAuthResolver` (single source of truth shared by `UpstreamForwarder` + `ImposterRouter`). Routing log now reports `auth=` (`Bearer`/`ApiKey`/`none`/`caller-passthrough`); `auth=none` flags an imposter route with an empty `Secret` (sends no auth header → upstream 401). | — |
| 2026-06-20 | When auth is managed (provider/override secret applied), the forwarder now strips the caller's `chatgpt-account-id` (`ManagedAuthIdentityHeaders`). Codex (`codex_sdk_ts`/`codex_cli_rs`) relays it alongside its own Bearer; an OpenAI-compatible imposter upstream (opencode) honoured it over the managed key and 401'd. Kept on key-less passthrough. Added a Debug-only masked outbound request dump to `UpstreamForwarder` for diagnosing forwarded-header issues. | — |
| 2026-06-20 | Added local synthesis for `GET /anthropic/v1/models` (HLD 005, Anthropic scope): distinct (ordinal, catalogue-order) union of Anthropic `to` targets emitted as a valid Anthropic List Models body, answered from config — no upstream call, no DB, no `Secret`, fixed `created_at` (byte-identical). Host `MapGet("/anthropic/v1/models")` recognizes the one case (wins over the catch-all for GET); `AnthropicModelCatalogResponder` aggregates string-out. Non-GET on the path and all other paths/dialects stay passthrough. OpenAI `/v1/models` (#20) not yet implemented on this branch. | #28 |
| 2026-06-20 | Decided: proxy does **not** sanitize tool function names (strict upstreams like Moonshot 400 on Codex's `_*`/`multi_tool_use.parallel` names). Fix is client-side; in-proxy rewrite is rejected because it would require breaking the no-response-rewrite non-negotiable (Codex dispatches by `function.name`). Docs only — no code change. | #19 (LADR-006 Accepted, LADR-007 Draft) |
| 2026-06-20 | Added request-only request normalization (HLD 004): `RequestNormalization` (`CodexToOpenAiSdk`/`None`) + `Normalization/` seam. v1 keeps only upstream-valid `function` tools (drop unsupported types, flatten `namespace`, drop names failing `^[A-Za-z_][A-Za-z0-9_-]*$`, clean dependent `tool_choice`); runs before Responses→Chat, imposter-only, idempotent. Third sanctioned request-rewrite class; supersedes HLD 001 LADR-006 for OpenAI imposter routes. Added L3 live-eval tier + `pr-evals-gate` workflow. | #19 |
| 2026-06-20 | Fixed Codex `/responses`→Chat 400 on `opencode-go`: the conversion now folds `role:"developer"` → `role:"system"` (Moonshot rejects `developer` with "tokenization failed"). Independent of tool normalization; covered by an L3 case that reproduces the full #19 failure (bad tools + developer role). | #19 |
| 2026-06-20 | **Amends HLD 004 LADR-03**: normalization is now **ON by default for `chat_completions`** (resolved in `ProviderCatalog`), `none` to opt out; `responses`/anthropic reject an explicit `codex_to_openai_sdk` (validator). Rationale: the reject rules are the generic OpenAI Chat Completions tool contract (openrouter/Bedrock 400 the same), and normalization is a no-op for clean clients — so opt-in per provider was the wrong default. `opencode-go` no longer needs the explicit flag. | #19 |
| 2026-06-20 | Implemented the HLD 004 LADR-05 bidirectional bridge: matched OpenAI imposter `/responses` requests downgraded to Chat now translate Chat Completions responses back to Responses SSE incrementally via `ChatToResponsesStreamTransformer`; all off-path responses remain byte-relayed. | #19 |
| 2026-06-20 | Implemented HLD 006 request-history normalization for `/responses`→Chat downgrades: paired tool Items become Chat-adjacent assistant/tool messages, orphaned tool history is removed, Responses state pointers/unknown Items fail fast, hosted/reasoning Items are removed by policy, and compatible `text.format` maps to Chat `response_format`. | #19 |
| 2026-06-20 | HLD 005 implemented: `GET /openai/v1/models` is answered locally from the route catalogue (distinct union of OpenAI `to` targets, first-declaring-provider `owned_by`, fixed `created=0`). Host registers a specific `MapGet` that outranks the catch-all; `IModelCatalogResponder` lives in Application (string-out, no `HttpContext`/upstream/credential seam). Anthropic discovery and non-GET on the OpenAI path still passthrough. | #20 |
| 2026-06-20 | Pinned two HLD 006 LADR-03 edge cases: hosted-tool removal is scoped to the `_call`/`_call_output` suffix — non-suffixed hosted Items (`mcp_list_tools`, `mcp_approval_request`) reject (fail-fast) rather than silently drop; a structured `function_call_output.output` array is JSON-stringified into the Chat `tool` content. Behavior pinned with L0 tests; no transformer logic change. | #33 |
| 2026-06-20 | Closed three HLD 006 NFR-03 request-field gaps on the `/responses`→Chat downgrade: reject the Conversations API pointer `conversation` (correctness, mirrors `previous_response_id`); convert `reasoning.effort` → Chat `reasoning_effort` for `minimal`/`low`/`medium`/`high`, drop `none`/unknown (`none` disables GPT-5.4+ tool calling); pass `stop`/`metadata`/`logit_bias`/`logprobs`/`top_logprobs` through as named allowlist additions. Real `/responses` and Anthropic stay byte-transparent. LADR-03 policy matrix extended. | #19 |
| 2026-06-20 | Fixed strict-upstream 400 (`message at position N with role 'assistant' must not be empty`, Moonshot): a Responses `message` Item that converts to empty content (null, empty, or only unsupported content parts — e.g. an empty `output_text`/refusal assistant turn beside a `function_call`) is now **dropped** on the `/responses`→Chat downgrade instead of emitted as an empty Chat message. Implements the previously-unbuilt gap-analysis row 2; drop applies to every role, never coerces placeholder content (LADR-02). | #19 |
| 2026-06-20 | HLD 007 (startup-config only; request path unchanged): `ImposterOptions.Providers` is now a name-keyed `Dictionary<string, ProviderOptions>` (was a positional `List`). `ProviderCatalog` route name = `Name` ?? key; `Name` is an optional `string?` display override. `ImposterOptionsValidator` rejects a legacy array / numeric keys (fail-fast migration message), case-only-duplicate keys/names, and a blank `Name` override. New `ImposterOptionsPostConfigure` (`IPostConfigureOptions`, runs before validation) applies the conventional `<NAME>_<FIELD>` env surface (`OPENCODE_GO_API_KEY`, …) over `IConfiguration` with precedence conventional > structured > appsettings; secret values are never logged (NFR-03). `Models[]` stays structured-only. | HLD 007 |
| 2026-06-20 | HLD 007 follow-up: invalid non-blank conventional `_IS_DEFAULT` values now leave the bound value unchanged and log a warning instead of appearing to apply silently; blank values remain ignored. | #42 review |
| 2026-06-20 | HLD 007 review follow-up: HLD 001 examples now show the required name-keyed provider object, Conductor env capture keeps structured prefixes as prefix matches, and `_IS_DEFAULT` post-configure uses the parsed bool directly. | #42 review |
| 2026-06-21 | Implemented HLD 008 Phase 1 runtime provider-config CRUD: `IProviderRegistry` seeds from resolved config/env once, `IOptionsSnapshot` overlays runtime state per request scope, catalog/resolver/model responders are scoped, `/admin/providers` is secret-free CRUD plus enable/disable, and disabled providers are excluded from imposter/default resolution. | #49 |
