# Routing — Feature Context

## TL;DR

Stateless same-dialect LLM router: reads the inbound `model`, rewrites it to a configured upstream
("imposter") or passes through to the dialect's default provider, optionally injecting prompt caching,
and streams the response back. Design rationale lives in `.docs/hld/001-llm-imposter-routing/`
(`README.md` index → `diagrams/`, `nfrs/`, `ladrs/` subfolders).

## Non-Negotiables

- **Never persist, log, or echo `ApiKey` values.** They live only in `ImposterOptions` (config/env). Logs
  carry provider name + model names only. The inbound caller's own `Authorization`/`x-api-key` is **not**
  forwarded — the provider's configured key replaces it.
- **Same-dialect only.** Do not add OpenAI⇄Anthropic body translation here. An `openai` provider serves
  openai requests; an `anthropic` provider serves anthropic requests.
- **`BaseUrl` is the server root WITHOUT a version path** (`https://api.openai.com`, not `.../v1`). The
  inbound request path is appended verbatim; adding `/v1` to config double-prefixes the path.
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
  The **shipped `appsettings.json` declares no defaults** (type-only impostering → 404 on unmatched; HLD LADR-005);
  `IsDefault` stays supported in code for deployments that opt back in.
- **Caching is per-dialect** (only when `Caching: true`): Anthropic injects ephemeral `cache_control` on the
  `system` block (a string `system` is converted to a one-element block array) and on the last content block
  of the last message; OpenAI sets `prompt_cache_key` to the **inbound** model name.
- **Errors are dialect-shaped**: OpenAI `{error:{message,type}}`, Anthropic `{type:"error",error:{type,message}}`.
  Routing failures → 400/404; upstream transport failures → 502.
- **`anthropic-version`** header defaults to `2023-06-01`, overridable per provider via `AnthropicVersion`.

## Credential Overrides

- **HLD 002 — credential persistence & overrides** (`.docs/hld/002-credential-persistence-overrides/`, status
  *Accepted*) reintroduced EF Core + PostgreSQL for stored **passthrough** credentials and added the
  Mediator-based `/admin/credentials` API. Routing has exactly **one credential seam**: after no-match →
  default/passthrough resolution, `ImposterRouter` consults `ICredentialStore` for the active dialect credential
  and passes a decrypted `RouteCredentialOverride` to the forwarder. **Do not** extend this to matched-imposter
  routes — those stay config-key-only and DB-free (HLD 002 LADR-004). The hot-path non-negotiables above are
  unchanged; the admin API uses Mediator/FluentValidation while routing stays raw (HLD 002 LADR-005).

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
