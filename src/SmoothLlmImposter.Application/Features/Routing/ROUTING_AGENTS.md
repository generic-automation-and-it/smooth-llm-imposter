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
  headers and body to the upstream **unchanged**, with exactly two exceptions: (1) the **auth** header is
  managed (see below), and (2) **caching injection** rewrites the body on a matched imposter route. Adding a
  bespoke filter that drops a caller header (e.g. `anthropic-beta`) breaks beta body fields like
  `context_management` — only the fixed hop-by-hop/content set (`Host`, `Content-*`, `Connection`,
  `Transfer-Encoding`, `Accept-Encoding`, …) is withheld. The caller's own `anthropic-version` passes through;
  the default `2023-06-01` is supplied **only** when the caller omitted it.
- **Auth header is the one managed header, route-dependent:** a matched imposter route sends the provider's
  configured `Secret` (never the caller's). On **passthrough**: the HLD-003 override ON ⇒ active stored Bearer; else
  a configured provider `Secret` / stored credential if present; else the caller's own `Authorization`/`x-api-key`
  is forwarded verbatim, so a key-less router authenticates with the caller's credential.
- **Auth *scheme* is decoupled from `Dialect`.** Provider config carries `Secret` + optional `AuthScheme`
  (`Bearer` → `Authorization: Bearer`, `ApiKey` → `x-api-key`). The forwarder resolves the scheme as
  `credentialOverride.AuthScheme ?? provider.AuthScheme ?? dialect default` (openai → Bearer, anthropic → ApiKey).
  So an `openai`-dialect upstream (e.g. opencode) can authenticate with `x-api-key` via `AuthScheme: ApiKey`
  without changing its wire dialect. There is no `ApiKey` config alias — `Secret`/`AuthScheme` is a breaking rename.
- **Same-dialect only.** Do not add OpenAI⇄Anthropic body translation here. An `openai` provider serves
  openai requests; an `anthropic` provider serves anthropic requests.
- **OpenAI Responses→Chat compatibility is explicit per provider.** `OpenAiUpstreamApi` defaults to
  `responses`. Set `OpenAiUpstreamApi: chat_completions` only for OpenAI-compatible upstreams that lack
  `/responses` (e.g. OpenRouter/opencode). On matched imposter routes only, `/responses` is forwarded to
  `/v1/chat/completions` and common Responses `input`/`instructions` payloads are converted to Chat
  Completions `messages`. Passthrough/default routes stay transparent.
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

## Key Behaviors

- **First match wins, in configuration order.** The resolver scans the dialect's providers top-to-bottom and
  returns the first `Models[].From` that matches; order providers/mappings from most to least specific.
- **`From` matching** is exact or single trailing-`*` wildcard (`claude-haiku-*`), case-insensitive (`ModelMatcher`).
- **No match → default passthrough** (model unchanged, no caching) via the dialect's `IsDefault` provider.
  No match **and** no default → `RoutingException(404)`. At most one `IsDefault` per dialect (startup-validated).
  The shipped `appsettings.json` declares **catch-all key-less defaults** for `anthropic` (`api.anthropic.com`)
  and `openai` (`api.openai.com`); remove them for type-only impostering (404 on unmatched; HLD LADR-005).
- **Caching is per-dialect** (only when `Caching: true`): Anthropic injects ephemeral `cache_control` on the
  `system` block (a string `system` is converted to a one-element block array) and on the last content block
  of the last message; OpenAI sets `prompt_cache_key` to the **inbound** model name.
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
- **Forwarder method/body**: `UpstreamForwarder.SendAsync` takes the inbound `HttpMethod` and a nullable body;
  `Content` is attached only when the body is non-empty. GET probes therefore reach the upstream as real GETs.

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
- **L0** `Application.UnitTest/Routing` — resolver, transformers (cache injection), router, error factory, options validator.
- **L2** `Host.IntegrationTest` — full pipeline incl. SSE passthrough and env-over-appsettings override (in-process stub upstream).

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
