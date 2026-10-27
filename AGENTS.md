# AGENTS.md

This file provides guidance for AI coding agents working in the SmoothLlmImposter repository.

> **Note on remaining `Project` placeholders.** This repo was scaffolded from a template. The functional/buildable identifiers (solution, projects, namespaces, folders) have been renamed to `SmoothLlmImposter`. Lowercase `project` references and examples still remaining in `.agents/rules/**` and `docs/**` prose are deferred template cleanup, not real references — replace them opportunistically when touching those files.

## TL;DR

.NET 10 NativeAOT LLM router. Every change must preserve compatibility with the published native executable, not just the local JIT host.

## Agent Response Format

1. Prefix every question to the user with `Q` and an incrementing number (`Q1`, `Q2`, `Q3`, …), so the user can answer by number.
2. Write statements as numbered lists (`1.`, `2.`, `3.`), never as bullets (`-`, `*`, `•`).

## Non-Negotiables

- **NativeAOT is the deployment baseline.** The published container runs a native executable without the .NET JIT runtime. New features, dependencies, data access, serialization, configuration binding, DI, and observability changes must remain compatible with that deployment.
- **Prefer build-time generation and explicit registration over runtime code generation or assembly scanning.** Follow the Host/Application source-generation contracts for JSON metadata, configuration binding, validators, and generated handlers. A reflection fallback that works under JIT is not evidence of NativeAOT support.
- **Validate the native artifact when changing an AOT-sensitive path.** Passing `dotnet build`, `dotnet test`, or `dotnet run` alone is insufficient: publish with `PublishAot=true` and exercise the affected behavior on the resulting executable/container. Preserve native-runner verification for both `linux/amd64` and `linux/arm64`, including native-library dependencies. Do not disable AOT or add broad trim/AOT-warning suppressions to hide failures.
- **AOT data access needs an AOT-compatible provider and mapping path.** If using Dapper in the native Host, use Dapper.AOT's generated supported calls and verify interception; ordinary reflection-based Dapper is not an acceptable implicit fallback. The existing optional EF Core `ImposterDb` backend is JIT-only and explicitly fails under NativeAOT; it does not establish a pattern for new native-runtime features.

## Project Overview

SmoothLlmImposter is a **stateless, key-less LLM request router**. It exposes OpenAI- and Anthropic-dialect endpoints and, per configuration, redirects an inbound model to an alternate upstream ("imposter") — rewriting the model name and optionally injecting prompt caching — or passes it through to the real provider. Unlike the Smooth Claude Proxy it stores nothing by default (keys come from config/env only; the sole exception is the opt-in GitHub Copilot feature, which persists encrypted OAuth credentials in a local SQLite file when `ENABLE_GITHUB_COPILOT=true`) and routes within a single dialect, with one opt-in exception: a matched Anthropic imposter route whose provider sets `AnthropicUpstreamApi: chat_completions` bridges Messages→Chat Completions and the reply back (HLD 016; no OpenAI⇄Anthropic translation, passthrough and native `messages` routes stay byte-identical). See `src/SmoothLlmImposter.Application/Features/Routing/AGENTS.md` and `src/SmoothLlmImposter.Application/Features/Routing/Bridge/AGENTS.md`.

**Tech stack:** .NET 10 NativeAOT · ASP.NET Core minimal APIs · Clean Architecture (Domain / Application / Infrastructure / Host) · `IHttpClientFactory` streaming forwarder (no DB on the default route; opt-in Copilot uses local SQLite) · Serilog · xunit.v3

## AI Context Files

Keep nested `AGENTS.md` files synchronised with code and documentation changes. Load the closest functional context before reading or editing feature code; the repository no longer vendors the `load-agents-context` hook or its generic skill. Standard global tooling setup is documented in `COLLABORATION.md`; repository-owned skills are listed in `.agents/skills/AGENTS.md`.

### Required Maintenance

- Every PR should create or update at least one nested `AGENTS.md` file.
- Update the closest context file to the code you change. Prefer local context over adding more content to this root file.
- When domain model or structural shape changes, also update the relevant implementation or architecture context.

### Placement Rules

- Functional feature context belongs close to the feature code.
- Cross-cutting concerns belong under `docs/hlds/02-nfrs/` or the nearest `AGENTS.md`.
- Avoid creating duplicate context files that restate the same plan at multiple levels without adding new information.

## Implementation Docs

All planned work is tracked as worktasks under `.context/work-tasks/` (gitignored — local only). Use `/create worktask` to scaffold a new one from the template.

Setup docs in `docs/wiki/setup.md` and `docs/wiki/setups/` should keep client base-url examples aligned with
the run mode's published host port, including Codex `openai_base_url` and `ANTHROPIC_BASE_URL` examples where
the guide is meant to be used by agent clients.
Conductor sandbox setup docs should keep general snapshot tooling (including Docker CLI + Compose v2) separate
from SmoothLlmImposter-specific imposter runtime setup. Agent-tooling steps must be split by scope: anything
repository-scoped (a tool that records an absolute repo path in its config, or writes into the working tree)
belongs in the workspace lifecycle, because the snapshot is built before any clone exists.
The `.conductor/` kit was removed in #125; maintain the paste-in lifecycle scripts in
`docs/wiki/setups/conductor.build-smooth-llm-imposter.md`, not a recreated kit. Document that existing UI
scripts need replacement/re-run when container environment forwarding changes.
Claude/Anthropic setup sections should also document that `claude setup-token` can create a Claude subscription
token, which users may supply explicitly as an imposter provider `Secret` with the matching `AuthScheme`.

## Repository Layout (Navigation)

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/SmoothLlmImposter.Domain/` | Pure routing model — `ApiDialect`, `ProviderRoute`, `RouteDecision`, `ModelMatcher` |
| Application | `src/SmoothLlmImposter.Application/` | Routing pipeline in `Features/Routing/` — options, catalog, resolver, transformers, router |
| Infrastructure | `src/SmoothLlmImposter.Infrastructure/` | `UpstreamForwarder` — `IHttpClientFactory` streaming forwarder (no DB on the default route); opt-in Copilot store/clients under `Persistence/GithubCopilot` and `Clients/GithubCopilot` |
| Host | `src/SmoothLlmImposter.Host/` | Minimal-API dialect endpoints, options binding + `ValidateOnStart`, Serilog |

Detailed backend coding rules are maintained in `.agents/rules/backend/` and scoped per-file via frontmatter (see Rules section).

## Rules

All rules live under `.agents/rules/` as `*.instructions.md` files and are auto-loaded every session by Claude Code, Cursor, Copilot, and Codex via the symlinks/path-references documented in `.agents/AI_DEVELOPMENT_AGENTS.md`. Applicability is scoped **per-file** via frontmatter (`paths` for Claude, `globs`+`alwaysApply` for Cursor, `applyTo` for Copilot) — e.g. backend rules carry `**/*.cs` so they attach when a C# file is opened. Rules are organized into category subfolders for navigation; the folder is organizational only and does not change loading. One exception to "auto-loaded every session": prompt-scoped rules may be **deferred for Claude** and re-injected on demand by a `UserPromptSubmit` hook (e.g. `code-review-standards` loads only on review prompts via `.agents/hooks/code-review-standards-context.sh`; Cursor/Copilot still load it always). See `.agents/rules/meta/rules.instructions.md` for the directory contract, file convention, and hook-deferred rules.

### Rule Categories

| Category | Folder | Contents |
|----------|--------|----------|
| _(cross-cutting)_ | `.agents/rules/` (flat) | `ai-workflow-rules`, `code-review-standards` (Claude: hook-deferred to review prompts), `project-overview` |
| git | `.agents/rules/git/` | `git-policy`, `pr-standards` |
| meta | `.agents/rules/meta/` | `rules` (file convention), `knowledge-conventional-contexts-quality` (AGENTS.md quality) |
| backend (`**/*.cs`) | `.agents/rules/backend/` | `api-mediator-validation` (Minimal API + Mediator + FluentValidation fail-fast); `architecture-slices` (clean-architecture boundaries, vertical-slice Features); `backend-logging-conventions` (Information vs Debug levels); `external-api-clients` (Refit list vs singular client split, HybridCache adapter); `migrations` (`[ExcludeFromCodeCoverage]` requirement); `wiremock-stubbing` (TestFramework.Aspire single-source stub helper) |

## Build / Test Commands

```bash
dotnet build SmoothLlmImposter.slnx                 # build
dotnet test  SmoothLlmImposter.slnx                 # run all tests
dotnet run --project src/SmoothLlmImposter.Host     # run the router locally
```

These development commands use JIT; they do not prove deployment compatibility. For changes affecting an AOT-sensitive runtime path, also publish and smoke-test the native executable on the host platform:

```bash
dotnet publish src/SmoothLlmImposter.Host -c Release -p:PublishAot=true
```

The container release path additionally targets Linux musl on both native architectures; a successful host-platform publish is not proof that every container native dependency is packaged correctly.

Target a single test project directly when needed (e.g. `dotnet test tests/SmoothLlmImposter.Domain.UnitTest`); `ls tests/` lists them. Tests need no Docker or external services — integration tests stub the upstream transport in-process, and the only on-disk database is the L1 tier's temporary SQLite files.

Container builds intentionally mirror the repo-root layout inside the SDK stage (`src/SmoothLlmImposter.*` under a non-`/src` working directory). When editing `Dockerfile`, keep `SmoothLlmImposter.slnx`, `Directory.*.props`, `NuGet.Config`, and `src/` in their repo-root-relative positions so solution/project references and central package props continue to resolve.

## Test Framework

xunit.v3 · Shouldly · Bogus. Tiers (the distinction drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, all in-process (Domain / Application / Infrastructure / Host).
- **L1** `SmoothLlmImposter.Infrastructure.ComponentTest` — real file I/O against temporary on-disk SQLite for the opt-in GitHub Copilot store (schema, transactions, reopen). File-I/O tests belong here, never in a `*.UnitTest` project.
- **L2** `SmoothLlmImposter.Host.IntegrationTest` — boots the real Host in-process via `WebApplicationFactory` and swaps the `imposter-upstream` HTTP client for a stub transport. No containers; the default route is stateless and key-less, so only opt-in Copilot cases use a database (temporary SQLite).

Shared fixtures live in `tests/SmoothLlmImposter.TestFramework/`. CI provisions a single WireMock service container (`127.0.0.1:19091`) for integration tests that stub upstream LLM endpoints over HTTP. See `docs/wiki/testing.md`.

## Style and Dependencies

Authoritative stack and coding conventions for AI coders are in `.agents/rules/project-overview.instructions.md` and backend-specific rules under `.agents/rules/backend/` (scoped per-file via `**/*.cs` frontmatter).

## Architecture Decisions (NFRs)

Human-facing reviewer documentation lives in `docs/wiki/`. Detailed high-level designs, non-functional requirements, and lightweight architecture decision records live under `docs/hlds/`.

The [`docs/wiki/hlds.md`](docs/wiki/hlds.md) **HLD table is the human-facing index** for `docs/hlds/` (the README only links to it). **Keep it in sync** — when a new HLD is created, removed, or changes status (Discovery → Accepted → Completed, or → Cancelled), update the table in the same PR. A stale table makes the HLD folder harder to discover and contradicts the Drift Minimization rule below.

## CI/CD

PR gate — `.github/workflows/pr-gate.yml` (triggers: `pull_request` → `main`, `push` → `main`, `workflow_dispatch`): restore → build (Release) → test with coverage via the local action `.github/actions/test-with-coverage`, then publish + upload the coverage report. The job declares one WireMock service container (`127.0.0.1:19091`) as its only external dependency — no PostgreSQL/Redis/Aspire. Full step list, service ports, and local .NET tools: `docs/wiki/ci.md`.

Container image publishing — `.github/workflows/publish-image.yml` pushes `ghcr.io/generic-automation-and-it/smooth-llm-imposter` from the repo-root `Dockerfile`. Keep published tags multi-architecture for both `linux/amd64` and `linux/arm64`; each arch builds on a native runner and the manifests merge afterwards — never a single QEMU-emulated build, because the NativeAOT compiler crashes under user-mode emulation. The `:latest` tag is emitted only on an automatic `push` to `main`; a manual `workflow_dispatch` run (allowed from any branch) skips `:latest` and instead publishes a user-supplied pre-release version tag (e.g. `1.0.0-rc.1`) via the required `version` input, plus the short-SHA tag.

**Container deployment:** The SDK stage uses `sdk:10.0-alpine` and publishes a self-contained NativeAOT executable for the target musl RID. The runtime stage uses `runtime-deps:10.0-alpine` plus `icu-libs`, not the old JIT `aspnet` image. Keep this native deployment and its non-root runtime intact; the existing image has no in-container HEALTHCHECK, so probe `/health` externally.

## Git Constraints

This repository is hosted on **GitHub** at `https://github.com/generic-automation-and-it/smooth-llm-imposter`.

- **CLI tool:** Use `gh` (GitHub CLI) for PR and repository operations.
- **Issue/PR target:** create issues and PRs in *this* repo (auto-detected from `git remote`). Do **not** target a different repo unless the user explicitly names it — even when a linked design/tracker issue lives elsewhere.
- **PR template:** `.github/pull_request_template.md`
- **Code owners:** `.github/CODEOWNERS` — all files owned by `@generic-automation-and-it/project` (a GitHub *team* handle, not the repo URL)

## Glossary

<!-- TODO: Add domain-specific terms and abbreviations as the project evolves. -->

| Term | Description |
|---|---|
| Blueprint | A reusable, parameterised specification for a component or service |
| Catalogue | The collection of all blueprints and templates in this repository |
| Spec-driven | Development approach where machine-readable specifications are the source of truth |

## Changelog

- 2026-10-27: HLD 017 NFR-01 (cache-prefix stability) proven by L2 tests (WT-14, #209); tests and docs only, no production change. `MessageOptimizerCachePrefixStabilityTests` resends a Claude Code-shaped Anthropic history every turn (harness block, tool rounds) through a gate decline, an unusable optimizer answer, a rewrite discarded by validation, a gate failure, an optimizer failure, both circuits open and a trial call that closes them, and asserts the raw bytes of every earlier message are unchanged and the model-call counts match the script. Separate cases cover the sliding TTL with a manual clock (kept at 40+40 minutes, dropped after 61 idle) and that missing or credential-derived sessions are never cached. An unusable optimizer answer is deliberate: a 500 opens the circuit and the next turn's open-circuit path records the same original, so only a failure that does not trip the circuit isolates the on-failure record. 1,529 tests pass (L0/L1/L2). Seven mutations (gate decline, optimizer failure, circuit-open and validation-discard originals not recorded, sliding refresh removed, derived sessions cacheable, idle expiry removed) each fail the intended case; two survived the first draft and drove the invalid-answer turn and the derived-session case, and the Ralph loop added the validation-discard turn. A replay on the macOS ARM64 NativeAOT executable (env-only config, local stand-in upstream, optimizer and decision server) held byte-identity with gate 2 and optimizer 2 for three turns and 0 error log lines. Linux musl AMD64/ARM64 runners not exercised.

- 2026-10-27: Per-route message-optimizer opt-out implemented (HLD 017 Open item 6, WT-13, #207). `ProviderOptions.OptimizeMessages` (`bool?`, conventional `<PROVIDER>_OPTIMIZE_MESSAGES`) lets one provider's routes skip the opt-in message optimizer; only `false` opts out, so unset/`true` keep it on, and nothing is set automatically for request-billed upstreams such as Copilot. It follows the HLD 011 plumbing rule through the post-configure suffix map, `ProviderOptionsCloner`, both `/admin/providers` DTOs, `ProviderCatalog` and `ProviderRoute`, and reaches the strategy as `OptimizationRequest.Provider` from the Host seam. The check lives in `MessageOptimizationStrategy`, so the tool filter still contributes on an opted-out route. 1,525 tests pass (L0/L1/L2). Three mutations (clone line, strategy check, Host route hand-off) each fail the intended tests, and only the L2 test catches the Host hand-off. macOS ARM64 NativeAOT publish is free of IL/trim warnings and a native run read `false`/`true`/unset back through the conventional env and the admin API, with a PUT round-trip preserving the flag. Linux musl AMD64/ARM64 runners not exercised.

- 2026-10-26: Decision tool filter always keeps `ToolSearch` and `Agent` in addition to the configured `AlwaysKeep` (HLD 014 LADR-08 amendment). Diagnosis behind it: the filter had stopped filtering because Claude Code sessions created on 2.1.306 or later (Conductor bundled 2.1.308 on 2026-10-25) send a `DeferredToolPlaceholder` with `defer_loading: true`, which the existing LADR-07 skip honours; no PR regression. A probe with the placeholder removed showed the filter dropping both gateways (kept 5/12), which would leave every deferred tool and subagent unreachable. Probes also showed `lego-upstream-anthropic` (Bedrock) accepts `tool_reference` while `scott-anthropic` (vLLM) rejects it with 400. New L0 theory verified load-bearing by reverting the fix; 1,503 tests pass (L0/L1/L2). Not AOT-sensitive; no native publish run.

- 2026-10-25: Tool-filter classifier circuit breaker implemented (HLD 014 LADR-11, NFR-05). One failed classifier call now opens a consumer-local circuit for `CircuitBreakSeconds` (default 180, optional `DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS`); while open, passes skip with reason `circuit-open`, make no classifier request and never reach the shared gate, so a dead or misconfigured service costs one call per cooldown instead of the full 3x60 s retry budget per request. After the cooldown a single trial admission decides close-or-reopen; a cancelled trial releases the admission. Only service faults count: `HttpRequestException` with a 4xx status other than 408/429, any 5xx, transport failure, and a new `DecisionServiceTimeoutException` (the transport wraps the resilience library's timeout). Contention is a new `DecisionGateTimeoutException` and never opens the circuit; caller cancellation propagates. A success only closes the circuit when it came from the trial, so concurrent traffic cannot exceed the one-call-per-cooldown bound. Application owns the breaker in `Features/Routing/Decisions`; the shared transport profile is untouched, so a second decision consumer registers its own. Validation rejects 0/3601 and ignores the value while the filter is disabled; the reflection drift guard covers the new conventional variable. 1,310 tests pass (L0/L1/L2); three mutations verified the new tests load-bearing (skip check, 4xx classification, trial abandon); macOS ARM64 NativeAOT publish is warning-free and a native run against an unreachable classifier logged one Error open, then an Information `circuit-open` skip with no classifier request. The resilience pipeline keeps its own retry; a malformed answers envelope still fails open without opening the circuit (LADR-11's closed failure list). Linux runners not exercised.

- 2026-10-25: Merged `.docs/` into `docs/` (`hlds/`, `wiki/` moved as-is, `.docs/` removed); rewrote all `.docs/` refs to `docs/` (md, cs, slnx, Dockerfile, templates, hook); `/docs/hld/` singular fixed to `/docs/hlds/`, `.dockerignore` now ignores `docs/`, hook watches `docs/adr|nfr/`.
- 2026-10-25: HLD 017 (Decision-Gated Message Optimizer) authored in Discovery from #164 — design only, no runtime change. Opt-in `LLM_MESSAGES_ENABLED` stage that asks the decision model whether each user message the proxy has not handled yet can be tightened and has a separate optimizer LLM rewrite only its human spans, one message at a time, as a strategy running in parallel with the tool filter; the proxy validates every rewrite against the original, remembers and replays what it forwarded (no in-text marker), and fails open behind per-section circuits. It would become the seventh sanctioned request-rewrite class and the first to rewrite `messages` content; the routing transparent-proxy rule stays unchanged until the HLD is Accepted. Depends on HLD 015 (per-section profiles, circuits and admission gates) and HLD 014 (tool filter returns an edit) amendments. Index row added to `docs/wiki/hlds.md`.
- 2026-10-25: Added Agent Response Format: questions numbered `Q1`, `Q2`, …; statements as numbered lists, no bullets. Removed the root `CLAUDE.md` → `AGENTS.md` symlink and its recreation in `agents-setup.sh`/`.ps1`, `AI_DEVELOPMENT_AGENTS.md` and `.github/copilot-instructions.md`.

- 2026-10-25: HLD 016 ralph-loop review (converged at loop 5). Fixed: bridged routes forwarded the inbound query string (spec 01 §15/04 E01); the stream translator used an unbounded event channel and abandoned an 8-second delay/wait registration per chunk (NFR-02); in-band overflow was not detected at numeric statuses outside 400/413/422 (04 §7.5.1); in-band 2xx errors lost `retry-after`; non-stream choice-level `error` with no content became an empty 200 message (02 §2/§11.5); stream error frames could echo the configured key; in-band 401/403 stream errors lacked the upstream-credential wording; `SystemMessage` copied `cache_control` from the dropped attribution block; dead API and an unused parameter were removed. Overflow phrasings are now the spec-mandated data table. 1,267 tests pass (L0/L1/L2), host-platform NativeAOT publish warning-free. Deferred: live Claude Code session, NFR-02 p99/allocation measurement, body-less `GET /v1/messages` 405 and `count_tokens` without a model (both cannot resolve a route), and the decision filter's forced-tool protection (outside the branch diff).

- 2026-10-24: Decision-filter classifier logs now name the model, the classifier endpoint (userinfo and query dropped, so a credential in `DECISIONS_TOOLS_BASE_URL` never reaches the console) and a 12-character SHA-256 digest of the captured session id (`session none` without one). The digest lets a pass be traced to the session whose ordering and sticky caches it used, without logging raw session values. Routing AGENTS, HLD 014 and the setup doc updated; 908 tests pass (L0/L1/L2).

- 2026-10-24: HLD 015 shared decision-model integration implemented and Accepted (#156). The decision transport no longer belongs to the tool filter: Application owns a consumer-neutral `DecisionServiceOptions` profile and an `IDecisionServiceConsumer` opt-in, and `DecisionServiceDemand` (any consumer opted in) now drives transport validation and the resilience profile — previously the profile fell back to fixed 3 attempts / 60 s whenever tool filtering was off, and validation was gated on the filter's `Enabled`. The client reads the shared profile instead of the routing options, and the process-wide gate is a dedicated `DecisionServiceGate` singleton rather than a client field. The operator surface is unchanged (`Imposter:DecisionsTools:*`, `DECISIONS_TOOLS_*`, same defaults, precedence and failure messages); the named HTTP client is renamed `decisions-tools` → `decision-service`. Startup validation runs as a hosted service, not `ValidateOnStart`: the profile resolves through the routing options, so a second startup validator reported an invalid routing configuration twice inside an `AggregateException` (caught by the existing legacy-array L2 test). Test gaps closed: reflection drift guard over every `DecisionsToolsOptions` property (backed by a new `DecisionsToolsVariables` table) and absent/blank preservation for all ten variables. 904 tests pass (L0/L1/L2), up from 887; six mutations confirmed the new tests catch the defects they target; macOS ARM64 NativeAOT publish warning-free and native smoke (valid route, enabled-invalid startup failure, disabled-invalid inert, single routing failure) passed. Linux AMD64/ARM64 native runners not exercised.

- 2026-10-24: HLD 016 Messages→Chat bridge implemented (LADRs/NFRs Accepted). Opt-in per-provider `AnthropicUpstreamApi: chat_completions` (default `messages`) on matched Anthropic imposter routes; an effective `Bearer` auth scheme is required at startup and admin upsert (LADR-01; this corrects spec 01 §2's "Bearer or ApiKey" wording). The Host seam maps the request after planning/model rename, runs the decision filter on the **Chat** body, forwards once with an outbound header allowlist (content type, accept, managed credential, router session header), and maps the reply; `AnthropicRequestTransformer` skips cache-breakpoint injection on a bridged route and `forwardCacheControl` is the bridge's own opt-in. Request mapping follows spec 01's allowlist/validation order, deterministic tool-name and tool-id maps, mid-turn system mapping, image hoisting, placeholders for unsupported blocks, canonical key order and reasoning profiles selected only by config. Reply mapping (non-stream and stream, sharing ids/stop reason/usage arithmetic) emits Messages error envelopes, validated-or-dropped tool arguments, unsigned thinking and non-empty estimated usage; `POST /v1/messages/count_tokens` is answered locally with the shared estimator. Option plumbing covers `ProviderOptions`, the conventional env suffix map, `ProviderOptionsCloner`, both `/admin/providers` DTOs, the validator and route materialisation, with a GET→PUT round-trip test. Tests: 1,250 pass (L0/L1/L2), including the spec matrices, an end-to-end bridged stream, count_tokens, header allowlist and non-bridged transparency; macOS ARM64 NativeAOT publish is warning-free and a native smoke covered `/health`, `count_tokens`, a non-stream bridged turn and a streamed bridged turn. Spec gaps recorded: spec 04 §7.5.1 omitted "context limit" from the overflow keyword list (added so Form A/B re-render); a body-less `GET /v1/messages` cannot resolve a bridged provider, so 405 applies only once a model resolves the route (body-less requests stay passthrough); `count_tokens` without a `model` keeps the existing 400. Live Claude Code session, Linux musl AMD64/ARM64 native runners and real upstream cache-hit measurement remain external gates.

- 2026-10-24: HLD 016 (Anthropic Messages to Chat Completions bridge) authored in Discovery — design and normative spec only, no runtime change. Opt-in per-provider `AnthropicUpstreamApi: chat_completions` on matched imposter routes would let Claude Code use Chat-only upstreams; it narrows the "same-dialect only" rule, which stays in force until the HLD is Accepted. Nine LADRs (opt-in, seam and header allowlist, incremental streaming, validated tool arguments, Messages-shaped errors/usage, allowlist with placeholders, stateless tool identity, unsigned thinking with explicit vendor profiles, local `count_tokens`), four NFRs, and spec files 00-04 (edge-case matrices in 01-04) as the conformance test list. Spec derived from a behavioral analysis of an existing translating gateway, with its defects replaced by target behavior; Claude Code behavior assumptions (error recognition, empty-signature thinking, overflow phrasing) are unverified against a live CLI and listed as Open items. Index row added to `docs/wiki/hlds.md`.
- 2026-10-24: Renamed all prefixed functional context files to nested `AGENTS.md` (Claude Code auto-discovers `AGENTS.md`). 9 files via `git mv`: `APPLICATION/HOST/DOMAIN/INFRASTRUCTURE_*`, `CREDENTIALS/GITHUB_COPILOT/AUTHORIZATION_OVERRIDE/ROUTING_*`, `TEST_FRAMEWORK_*` → sibling `AGENTS.md`; filename H1s replaced with descriptive titles. All in-repo links updated; generic `*_AGENTS.md` convention mentions rewritten to nested `AGENTS.md` (root `AGENTS.md`, `COLLABORATION.md`, PR template, workflow/rules instructions, `architecture.md`, `testing.md`, `setup.md`, worktask template/hook, `TEMPLATE_README.md` naming/structure sections). Excluded: `.agents/AI_DEVELOPMENT_AGENTS.md` (slnx + docs reference), `.agents/templates/TEMPLATE_AGENTS.md` (template itself), `.github/CODE_REVIEW_GRAPH_AGENTS.md` + `WORKFLOWS_AGENTS.md` (two files, one `AGENTS.md` target — collision). Build clean, 0 warnings/errors; remaining 4 broken-link hits pre-existing and untouched.

- 2026-10-23: HLD 014 LADR-09 — opt-in sticky decision-tool selection per session (`DECISIONS_TOOLS_STICKY_PER_SESSION`, default off). The decision filter re-selected tools every user turn, so the forwarded `tools` array — the first thing in every provider prompt-cache prefix — could change on each turn; HLD 001 LADR-008 ordering could not help because it never keeps a tool and runs before the filter. Now, for a captured session, every known tool forwarded after a completed pass stays forwarded (not asked, not removed) while the harness still sends it; fail-open, partial fail-open, all-skip and small-catalog passes record nothing. The filter now receives caller headers and the **pre-transform** body: the transformed OpenAI body carries a router-injected `prompt_cache_key` = inbound model, which would have made every chat of a model one "session" (caught in plan review, guarded by an L2 test). The bounded per-session map was extracted from `ToolCatalogOrderer` into `SessionScopedMemory`/`SessionScope` and is shared. Conductor installer forwards the tenth `DECISIONS_TOOLS_*` variable; existing pasted scripts must be replaced and re-run. 881 tests pass (L0/L1/L2); six mutations verified the new tests load-bearing; macOS ARM64 NativeAOT publish warning-free and a native smoke (env and structured binding) showed second-turn retention and cross-session isolation. Upstream cache-hit gain remains unmeasured.

- 2026-10-23: HLD 014 decision filter: tool selection made robust to a poorly calibrated classifier (HLD 014 LADR-08). In a long Claude Code session `kev-4b` scored all 57 asked tools below the container's 0.60 threshold, so the 7 history-protected tools were all that remained (7 of 64). Counting only asked tools in the all-skip guard was tried and rejected: 3 of 4 measured vague requests then forwarded 64/64 tools. Instead `DECISIONS_TOOLS_ALWAYS_KEEP` (default `Bash,Read,Edit,Write,Skill`) are never asked about or removed, `DECISIONS_TOOLS_MIN_KEEP` (default 20) tops the kept set up by score (counting core and history-protected tools; catalogs within it skip the classifier), and `MinProbability` returns to 0.50. Replayed on the native binary against live `kev-4b` with a 64-tool catalog and 7 tools already used: 20/64 forwarded for all four requests, core 7 always present. Measured but not changed: earlier assistant text dominates the ranking (four unrelated requests shared 93% of their top-13 tools; request-only state 32%). Each new rule verified load-bearing by mutation; macOS ARM64 NativeAOT publish clean.

- 2026-10-23: HLD 014 follow-up from live LM Studio logs after #142. Two more harness wrappers were reaching the classifier state ahead of the human request: Conductor's `<system_instruction>` block and Claude Code's local-command echo (`<local-command-caveat>`, `<command-name>/<command-args>`, `<local-command-stdout>` for commands like `/model`). Both are now stripped from the classifier's copy; a `<command-name>/<command-args>` block is stripped only when local-command output directly follows it (classified on the raw text before stripping), because a skill invocation's command and arguments are the request, even when Claude Code merges it into the same user message as a `/model` echo. The same logs showed every pass failing open with a bare `HttpRequestException` — the configured `openjev-flash-9b` is listed by LM Studio as `llm`, not `decision`, so `/v1/systemone` answered `400 invalidModelIdentifier`; the fail-open warning now names the HTTP status and configured model, and `setup.md` documents the check. Each new rule verified load-bearing by mutation.

- 2026-10-23: HLD 014 decision filter reworked against real Claude Code traffic (#138). The classifier state was the head of the CLAUDE.md `<system-reminder>` block that Claude Code puts ahead of the request in the same user turn, so the 2,000-character budget never reached the request; `kev-4b` scored every tool below 0.5 and a 0.60 threshold stripped the catalog (the session ended in an upstream `ResponseEnded`; its imposter logs were lost to a restart, so that link is unconfirmed). Captured Claude Code bodies (local capture server, no upstream) then showed further traps: a `role:"system"` `<total_tokens>` message after every tool result, hook output as system messages, and a tool-search catalog whose `defer_loading` placeholder the classifier scores 0.13. State is now `User request:` + `Earlier conversation (newest first):` — reminder blocks, tool-result turns, post-request assistant text and system text (fallback only) excluded, so the kept set stays fixed for a user turn; questions ask whether the assistant needs the named tool (300 chars, balanced criteria); an all-skip pass keeps the catalog; deferred-loading catalogs are skipped; tool answers are memoised in memory per question (model + state + question hash), so tool-call continuations reuse them and only valid answers are cached (HLD 014 LADR-07); only `tools`/`tool_choice` are rewritten, every other top-level value is forwarded byte-for-byte; default `MinProbability` 0.50 → 0.20; removed tool names logged. Live JIT run on a captured 54-tool body: 23/54 kept in 6.4 s, repeat from memo in 1 ms. Each new rule verified load-bearing by mutation. macOS ARM64 NativeAOT publish clean (no trim/AOT warnings); native run on the captured body kept 23/54 with identical memo replay. Linux musl/AMD64 and real upstream cache-hit measurement remain open.

- 2026-10-23: Conductor paste-in installer forwards all seven `DECISIONS_TOOLS_*` variables through direct Docker and sudo; Linux host-gateway mapping, default-off opt-in guidance, Bash 3.2-safe optional arrays; removed-kit references corrected. Existing UI scripts require replacement/re-run. #138

- 2026-10-23: HLD 014 classifier success logs now emit the canonical request body shape instead of a caller-controlled path, avoiding log-forging input in the default console sink. #140

- 2026-10-22: Decision-model tool pre-filter implemented (HLD 014, #138/#139). Default-off `Imposter:DecisionsTools` global tool pruning across Chat Completions, Responses and Messages, including passthrough; only positively understood, unprotected tools are removed, history is never rewritten, and understood choices are pruned conservatively. Questions are batched into fail-open 20-question chunks behind one process-wide serialized OpenJEV gate with complete-response timeouts. Application owns selection rules and the client contract under `Common/Clients/Decisions`; Infrastructure owns transport and response buffering; Host invokes the filter at the existing post-transform seam. Enabled classifier `BaseUrl` now requires an absolute HTTP(S) URL (`file:`/`ftp:`/`mailto:` fail startup); disabled settings stay unvalidated and no shipped `appsettings*.json` enables the feature. Added HLD 015 (Shared Decision-Model Integration) in Discovery — a consumer-neutral internal ownership proposal that does not supersede HLD 014 or change runtime behavior. Reviewed: 790 tests pass across L0/L1/L2 with a warning-free build; macOS ARM64 and Linux musl ARM64 NativeAOT live checks are recorded in HLD 014. Linux AMD64 native-runner verification and real upstream cache-hit measurement remain external gates, so HLD 014 stays Accepted. Known limit: an unrecognized forced hosted-tool choice can outlive its removed tool and be rejected upstream (LADR-06).

- 2026-10-21: Added `.agents/skills/AGENTS.md` identifying `imposter-newsession` and `imposter-who` as repository-owned; removed the requested non-owned skill copies and their two dependent hook wrappers/registrations. Moved contribution guidance to `COLLABORATION.md`, including recommended global installation of standard skills, rules, and hooks; README links there. Functional context must still be loaded explicitly when no external context loader is installed.

- 2026-10-20: GitHub Copilot opt-in integration implemented (HLD 013, #126). Adds reserved `github-anthropic`/`github-openai` provider pairs, GitHub device login, encrypted local SQLite credentials in a dedicated volume with an isolated Data Protection key ring, startup hydration and refresh, ordered admin APIs with a `message` help field, Scalar/OpenAPI, and managed Bearer routing for `/v1/messages` and `/responses` (Copilot Chat excluded). Matched Copilot routes resolve auth/origin from a published in-memory snapshot — never a per-inference DB lookup — a narrow documented exception to HLD 002 LADR-004. Persistence is direct `Microsoft.Data.Sqlite` (Dapper.AOT spiked and rejected: no .NET 10 interceptors). Verified: the full deterministic suite across L0/L1/L2, a host-platform NativeAOT publish, and a native run exercising `/health`, the Copilot admin endpoints, Scalar/OpenAPI and real GitHub device-code rejection. Live own-OAuth-app probes and Linux musl container runs remain external gates.

- 2026-10-20: README slimmed. HLD index table moved to `docs/wiki/hlds.md`; the stale Conductor-kit install section was dropped (the kit was removed in #125); the Customized Switches section now documents the live `--who?`/`--newsession` family (HLD 010 is Completed) instead of a not-yet-implemented preview; Quick start setups table gained stable `S01…` IDs; GitHub Copilot section marked foundation-only.

- 2026-10-20: Test tiers are now L0/L1/L2. Added L1 `Infrastructure.ComponentTest` (temporary on-disk SQLite for the opt-in GitHub Copilot store) and narrowed the "no DB" statements to the default stateless route; the router itself remains key-less unless `ENABLE_GITHUB_COPILOT` is on.

- 2026-10-19: Made .NET 10 NativeAOT the explicit deployment baseline for future changes. Added cross-cutting source-generation/dependency/data-access guardrails and native publish/runtime verification requirements; distinguished JIT development checks from native compatibility and corrected the stale JIT container description. No runtime implementation changed.

- 2026-10-17: NativeAOT publish supported (opt-in: `dotnet publish -p:PublishAot=true`, ~40 MB self-contained binary). It compiled before but crashed at startup — RDG endpoint metadata had no source-gen fallback (`NotSupportedException` on `ProviderConfigurationBody`). Fixed with `ImposterJsonContext` covering all endpoint DTOs, a `HealthResponse` record for `/health` (wire-identical `{"status":"ok"}`), explicit per-request validator registration (assembly scanning is trim-unsafe; `ValidatorRegistrationTests` guards the list), code-applied Serilog levels (replacing `ReadFrom.Configuration`, whose assembly scanning finds nothing in single-file AOT), `(JsonNode)` casts on 19 `JsonArray.Add(JsonObject)` sites, and a clear fail-fast for the EF Core `ImposterDb` path under AOT. AOT publish is warning-clean under `TreatWarningsAsErrors` (third-party package trim codes `NoWarn`-scoped to `PublishAot` builds only); AOT↔JIT live parity verified (`/health`, both `/v1/models`, `--who?` both dialects, admin upsert/get/list/delete, validation 400). 446 tests pass (445 + providers-admin 400 case from review). The `Dockerfile` now ships AOT too (per-`TARGETARCH` musl RIDs on `runtime-deps` + `icu-libs`, ~73 MB vs 131 MB JIT; container smoke-tested non-root, including a real stub-upstream forward + SSE relay). Config binding needed the source generator (`EnableConfigurationBindingGenerator` + `Configure<T>(section)` — `AddOptions().Bind()` is not intercepted) and mutable setters on options leaves (the generator silently skips init-only properties, so `Models[]` bound empty on AOT while JIT worked). Not yet in CI as a probe job. Pre-existing, untouched: `NU1903` audit failures on transitive `System.Security.Cryptography.*` (restore needs `-p:NuGetAudit=false`), and the Serilog request log records `500` on validation failures on both JIT and AOT while the client correctly gets `400`.

- 2026-09-22: L3 eval gate fixed — `opencode-go` now rejects session-less traffic with
  `400 MissingSessionID` ("Request is missing x-opencode-session and cannot be routed efficiently"), so
  `OpencodeToolNormalizationEvalTests` failed on every run after 2026-08-30. Not a proxy regression: the eval
  posts directly with its own `HttpClient` and never touches `UpstreamForwarder`; reproduced locally with the
  same key. `PostAsync` now stamps `x-opencode-session` (fresh `Guid.NewGuid().ToString("N")` per request) —
  the same header the proxy sends on a matched `SessionForwarding: opencode-go` route (HLD 009), so the eval
  stays on the path real traffic takes. The worse half was silent: the companion "un-normalized catalog is
  still rejected" case was **passing vacuously** — it asserts `400` and was getting one for `MissingSessionID`,
  never reaching the tool contract it exists to prove. It now also asserts the body is not a `MissingSessionID`,
  verified by removing the header again (both tests fail; previously one went green). The real rejection still
  reads `unknown tool type: web_search, currently only function and plugin are supported`. Also fixed the
  review-gate block on HLD 012's `AGENTS.md`: it lacked the mandatory `## Changelog` section, and `Defaults`
  was renamed to the canonical `Key Behaviors` slot per
  `knowledge-conventional-contexts-quality.instructions.md`.

- 2026-09-22: Review pass on the HLD 012 timeout work — the mechanism is correct, but its only end-to-end
  proof was inspection. `UpstreamForwarderTimeoutTests` asserted the stamp and `DependencyInjectionTests`
  called the generator helpers directly; nothing drove Polly, so the seam between them was untested.
  Added `UpstreamTimeoutPipelineTests` (L0, Infrastructure): builds the real `AddInfrastructure` container,
  swaps only the primary handler (`ConfigurePrimaryHttpMessageHandler` is additive, so the resilience handler
  stays), and stalls the first attempt. It asserts two framework behaviours the design leans on and that no
  other test would catch — `context.GetRequestMessage()` is populated inside the timeout strategy, and the
  **retried** request still carries `Options`. Without the second, a tuned provider would get
  `base/default/default` rather than `base ×1/×2/×3`: configurable, non-inert, and still wrong. Both
  behaviours hold on the shipped `Microsoft.Extensions.Http.Resilience`. Verified load-bearing — with the
  forwarder stamp removed the test fails (1 upstream call instead of 2). It is wall-clock dependent by
  nature (a timeout only proves itself by elapsing), kept to ~3 s by the validator's 1 s floor and written
  without fixed sleeps: the stub waits on its own cancellation token, with a 10 s ceiling that is far above
  the 1 s budget under test and far below the 300 s default. Also fixed doc drift the original change missed:
  `AGENTS.md` — the closest context file to `DependencyInjection.cs` — still documented the
  fixed 200/600/900 s ladder and gained no note of the per-request transport, and HLD 001 LADR-003 still
  stated 200 s and a 1,700 s worst-case header wait (now base ×6, 1,800 s on the shipped base). 444 pass.

- 2026-09-22: Per-provider upstream timeout (HLD 012). `ProviderOptions.TimeoutSeconds` (`int?`, conventional
  env `<PROVIDER>_TIMEOUT_SECONDS`, validated 1–3600) sets a provider's **base header timeout**; the resilience
  handler scales it ×1/×2/×3 across the three forward attempts. The global base moved 200s → **300s**, so the
  shipped ladder is **300/600/900s** — the first rung was the odd one out, and it is the wait most likely to
  declare a merely-slow upstream (a local LM Studio loading a large model) dead before any retry. The value is
  carried per request rather than per client: a named `HttpClient` per provider cannot work because providers
  are runtime-mutable (HLD 008) and one created through `/admin/providers` after startup would have no
  registered client — so `UpstreamForwarder` stamps `request.Options` and the Polly `TimeoutGenerator` reads it
  back via `context.GetRequestMessage()`. **That stamp is the load-bearing hop** — exactly the shape HLD 011
  shipped inert in — so it has its own test that fails without it. Timing out via a linked `CancellationToken`
  instead was rejected: it surfaces as `OperationCanceledException`, which the retry predicate does not handle
  and which is indistinguishable from a caller disconnect. Scope is deliberately time-to-first-byte only; the
  client keeps `Timeout.InfiniteTimeSpan` so SSE bodies stream unbounded. `0`/negative fails startup rather than
  falling back, because a discarded override and a too-low one present identically. Both drift guards
  (`ProviderOptionsClonerTests`, `ImposterOptionsPostConfigureTests` — the latter in both its suffix-mapping and
  bindable-field forms) were widened to `int`/`int?`; the bindable-field guard caught the missing suffix
  unprompted. The admin surface gates it twice: the FluentValidation body rule for a field-level 400, and
  `EnsureValidRegistry`, so a live upsert cannot install a value that would fail the next boot. Set in no
  shipped `appsettings*.json`. 17 new test cases (L0 ladder/base-resolution, validator range, catalog
  materialization, forwarder stamp, admin upsert; L2 admin round-trip), 5 of them verified failing with the
  forwarder stamp and cloner line removed; 442 pass.

- 2026-08-13: `.conductor/scripts/setup.sh` now materialises an optional `SSH_PRIVATE_KEY` into `~/.ssh/id_rsa`
  (`0600`, under a `0700` `~/.ssh`) before the Codex step, so git-over-SSH works in a cloud sandbox that starts
  with no `~/.ssh` at all. Two guards, both load-bearing: `${CONDUCTOR_IS_LOCAL:-0}` = `0` keeps it away from
  the developer's own Mac key, and `[ -n "${SSH_PRIVATE_KEY:-}" ]` is what stops the truncating redirect from
  replacing a working key with a zero-byte file on every workspace that injects no key. The variable is
  optional — nothing else in the kit needs SSH. The flattened paste-in copy and the script table in
  `docs/wiki/setups/conductor.build-smooth-llm-imposter.md` were updated in lockstep, and the guard is
  recorded as a non-negotiable in `.conductor/AGENTS.md`.
- 2026-08-12: A mid-stream upstream failure now ends the response deliberately instead of escaping the endpoint.
  Production symptom: `HttpIOException: The response ended prematurely. (ResponseEnded)` from
  `ChunkedEncodingReadStream` after ~146 s on `POST /anthropic/v1/messages` — the upstream ended a chunked SSE body
  without its terminating chunk. **Not a proxy timeout**: the `imposter-upstream` client uses
  `Timeout.InfiniteTimeSpan` and the Polly timeout wraps only `SendAsync` under
  `HttpCompletionOption.ResponseHeadersRead`, so it stops applying once headers land (and would throw
  `TimeoutRejectedException`, not `HttpIOException`). The existing mid-stream `catch` is gated on `RequestAborted`,
  so with the caller still connected the exception escaped to Kestrel *after* `Response.HasStarted` — which can
  only log a second unhandled stack trace and cut the socket, leaving the client with an unexplained truncation.
  `RoutingEndpoints` now catches `IOException`/`HttpRequestException` there and, in order: writes a normal
  dialect-shaped `502` JSON body when nothing had reached the wire; otherwise appends a terminal SSE error frame;
  otherwise (partially-written non-SSE body) logs only. New `IErrorResponseFactory.CreateStreamErrorFrame` +
  `StreamErrorFraming` shape that frame — framing is derived from dialect **and path**, not dialect alone, because
  OpenAI Chat streams carry unnamed `data:` frames while Responses streams carry named `event:` frames with the
  discriminator inside `data`, and the wrong shape is silently dropped by the SDKs. No `[DONE]`/`message_stop` is
  emitted, so a client ignoring the frame still sees an abnormal end. 8 new tests (3 L0 framing, 5 L2 —
  `StreamFailureTests`, all 5 verified failing without the Host change); 425 pass.
- 2026-08-10: `pipeline-code-review-report.yml` unpinned from the upstream commit SHA
  (`aa59e705…`) to `@main` in both the reusable `uses:` and `tools_ref:` — this project does not
  use action secrets/supply-chain pinning, and `main` keeps the review tooling current without a
  per-release sync step. `pipeline-ai-analyse.yml` already resolved its tooling checkout via
  `@main`/`SMOOTH_AI_REVIEW_TOOLS_REF`. Repo commit flow is `/git-commit-review-push` — it commits,
  embeds the `/ai-review` full-review trigger in the last commit, and pushes.
- 2026-08-10: HLD 011 review fixes — the feature shipped **inert**. `ProviderOptionsCloner.Clone` omitted
  `StripEncryptedContent`, and the HLD 008 registry seed (`ProviderRegistryStartupSeeder`, unconditional
  `IHostedService`) clones every provider before `ProviderCatalog` reads it, so the flag was always `null`
  on the route and the strip never ran. All 415 tests passed anyway because every test built `ProviderRoute`
  by hand, bypassing the seed. The existing `ProviderOptionsClonerTests` drift guard — written for exactly
  this class of bug — filtered `typeof(string) || typeof(bool)`, and `bool?` is neither, so it was blind;
  the same filter had been widened in `ImposterOptionsPostConfigureTests` but not here. Also fixed:
  `/admin/providers` DTOs (`ProviderConfigurationResponse`/`Body`) lacked the field, so the flag was
  unreadable via the API and an upsert of an opted-in provider silently cleared it; and a JSON-`null`
  `encrypted_content` was treated as ciphertext, dropping the plaintext summary of clients that serialize
  the property unconditionally. Coverage added at the two hops the original tests skipped —
  `ProviderCatalogTests` seed-materialization and the L2 `GET → PUT` round-trip; 4 tests fail without the
  clone fix. HLD 011 itself did not exist: the code and docs cited "HLD 011 / LADR-05" with no such folder,
  and `LADR-05` collided with unrelated LADR-05s under HLDs 004/008/010. Authored
  `docs/hlds/011-zdr-encrypted-content-sanitation/` (README, AGENTS, LADR-01 drop-whole-item, LADR-02
  independent-opt-in, LADR-03 no-validator-rule, NFR-01 no-decryption/no-logging, NFR-02 default-off
  transparency, stage-ordering diagram) and repointed every reference to LADR-01. The HLD 007 LADR-02
  conventional-env table was stale for five suffixes (`_AUTH_TOKEN`, `_AUTH_HEADER`, `_ENABLED`,
  `_SESSION_FORWARDING`, plus the new `_STRIP_ENCRYPTED_CONTENT`) and is now in sync with
  `ImposterOptionsPostConfigure.Fields`. The option stays **off by default and is set in no shipped
  `appsettings*.json`** — it is injected per deployment (`<PROVIDER>_STRIP_ENCRYPTED_CONTENT=true`) and
  documented in `docs/wiki/setup.md`. 417 tests pass.
- 2026-08-09: Added `StripEncryptedContent` per-provider option (HLD 011) — drops OpenAI ZDR
  `encrypted_content` reasoning items on the `/responses` forward path so an upstream that cannot decrypt
  (e.g. LM Studio) no longer rejects with HTTP 400 "Encrypted content is not supported." Independent of
  `OpenAiUpstreamApi` and `RequestNormalization` (not gated on `IsImposter`); composes before the
  `chat_completions` downgrade. The proxy has no decryption key (ZDR holds it server-side only), so it
  **drops** the whole reasoning item (LADR-01) rather than decode; plaintext/summary reasoning survives
  verbatim. Conventional env `_STRIP_ENCRYPTED_CONTENT` per provider. See
  `src/SmoothLlmImposter.Application/Features/Routing/AGENTS.md`.
- 2026-08-01: Kit release workflow fixed — it could never attach assets. Releases on this repo are
  **immutable**, so assets attach only *before* publication; `action-gh-release` created the release
  already published and every upload was rejected with "Cannot upload asset … to an immutable release".
  Because the action creates the release before uploading, the failure left `v0.0.1` and `v1.0.0`
  published with **no assets**, and those tag names are now permanently reserved — deleting the release
  *and* its tag does not free them (`tag_name was used by an immutable release`), so that cleanup attempt
  was wasted effort. Fix: create with `draft: true`, attach assets to the draft, publish in a second step
  via `gh release edit --draft=false`. `make_latest` moved onto the publish step, because the action
  applies it at creation while the release is still a draft — and `install.sh` with no `--ref` resolves
  through `/releases/latest`. Proven end to end: `v0.0.2` published with all three assets, and the
  documented `curl … --ref v0.0.2` install verified in a scratch directory (checksum OK, kit extracted,
  version stamped). Immutable releases are the correct repository setting; the workflow was wrong.
  Separately, comment density across `install.sh`, the four kit scripts and the workflow was cut from
  33-71% to 13-17%, with the removed rationale living in `.conductor/AGENTS.md` instead — verified
  comments-only, the non-comment diff against `main` for `.conductor/scripts/` is empty.
- 2026-08-01: Conductor scripts made restart-survivable, after a report that the imposter did not come back
  following a micro-VM restart. `imposter-container.sh` no longer passes `--pull=always` — it does a
  failure-tolerant `docker pull` *before* the unconditional `docker rm -f`, because `--pull=always` returns
  `exit 125` when the registry is unreachable even though the image is cached locally, so a transient GHCR/DNS
  blip destroyed the working container and refused to recreate it (verified both before and after the change).
  The `CONDUCTOR_IS_LOCAL` short-circuit was removed from `imposter-container.sh` and stays only in
  `setup.sh`, now read as `${CONDUCTOR_IS_LOCAL:-0}` — under `set -u` a bare reference aborted any hand-run
  with `unbound variable`, and a manual `restart-imposter` trigger that exits 0 silently is worse than one
  that tries and reports. The Linux-only daemon bootstrap and `DOCKER_HOST` default are now gated on
  `uname -s` so the container path can work against Docker Desktop on macOS (written, not yet run there).
  `dockerd` is now started under `setsid` (previously only `nohup`, which covers SIGHUP but not a
  process-group teardown). `.conductor/settings.toml` gained `default = true` on the run script plus
  `auto_run_after_setup = true`: `[scripts] setup` fires only on workspace *creation* and Conductor's
  repo-settings schema has no resume hook, so without a default run script nothing re-established the
  container after a restart. A second trigger, `imposter-logs` (`.conductor/scripts/imposter-logs.sh`),
  follows `docker logs -f` on the container after checking the daemon is up.
  Reordering `setup.sh` to start the container *before* Codex/`code-review-graph` was tried and **reverted**
  the same day: on a restarted micro VM it moved `docker pull`/`rm -f`/`run` to about a second after the
  daemon first answered `docker info`, and the daemon then vanished mid-health-check. The ordering is now
  pinned with a comment, the health wait tolerates a daemon blip (`--restart unless-stopped` brings the
  container back), and failures print a daemon-first `diagnose()` bundle — container status, `docker logs
  --tail 100`, whether a `dockerd` process exists, its log tail, kernel OOM lines — to the terminal, because
  the operator works in a remote sandbox without filesystem access. The prior code ended its failure path
  with a bare `docker logs`, which answers "Cannot connect to the Docker daemon" in exactly the case that
  matters and silently discards the container output. Root cause of the daemon death is still unconfirmed.
  `.conductor/AGENTS.md` and `docs/wiki/setups/conductor.build-smooth-llm-imposter.md` (including its
  embedded paste-in copy) updated to match.
- 2026-08-01: Packaged the `.conductor` kit for distribution to other repos, **shipping the scripts
  as-is**. Added `.conductor/install.sh` (vendoring installer with `--ref` and `--check`; extracts
  into `.conductor/`, verifies SHA256, stages before swapping) and
  `.github/workflows/publish-conductor-kit.yml` (tarball + `SHA256SUMS` attached to a GitHub Release
  on a `v*` tag; `workflow_dispatch` publishes a pre-release from any branch). `.conductor/AGENTS.md`
  split into generic kit context and repo-specific context. `README.md` documents the remote install
  under Quick start. `install.sh` is attached to each Release **as its own asset**, not only inside the
  tarball — it is the bootstrap, so a consumer needs it before they have the archive; shipping only the
  archive left the documented `releases/download/<tag>/install.sh` URL a 404. The pre-release bootstrap
  is `raw.githubusercontent.com/.../main/.conductor/install.sh`, which needs no tag to exist.
  An attempt to make the kit repo-agnostic first — replacing the hardcoded `-e Imposter__Providers__*`
  flags with an `Imposter__*` environment pass-through — was **reverted in full**, along with the
  `setup.sh` worktree fix and conditional claude-code flags that rode with it. The pass-through was
  broken in two independent ways: `grep -oE '^Imposter__[A-Za-z0-9_]+'` truncated every key at its
  first hyphen (provider names all contain them), forwarding a non-existent variable so every route
  silently vanished; and `done < <(…)` cannot work in this sandbox because there is no `/dev/fd`. The
  deeper problem was product shape, not implementation: a kit that routes nothing until the consumer
  authors and places a mapping file is worse than one that works on install. Provider keys also cannot
  be expressed as shell variables at all (`export 'Imposter__Providers__opencode-go-anthropic__…'` →
  "not a valid identifier"), which the externalisation design had not accounted for.
  Two items are left open and recorded in `.conductor/AGENTS.md`: `setup.sh` still writes
  `.git/info/exclude` by literal path, which aborts setup inside a git worktree (i.e. every local Mac
  workspace), and whether a user-level `~/.conductor/settings.toml` reaches a cloud sandbox remains
  untested.
- 2026-07-31: Added supply-chain provenance version vars: `OPENCODE_CLI_VERSION`, `OPENCODE_TOOL_CODE_REVIEW_GRAPH_VERSION`, `OPENCODE_TOOL_RTK_VERSION` to `pipeline-ai-analyse.yml` env block (reading from repo vars). `pipeline-code-review-report.yml` documents the vars in its config comments (thin caller, env exports live in the upstream reusable workflow).
- 2026-07-31: The `opencode-go-openai` provider was split into two keys — `opencode-go-openai-chat`
  (`OpenAiUpstreamApi: chat_completions`, the explicit default) and `opencode-go-openai-responses`
  (`OpenAiUpstreamApi: responses`, for future `gpt-5.6-luna` testing) — in `appsettings.json`,
  `appsettings.Development.json`, and the Conductor scripts. Confirmed both OpenCode Go (422 on
  Responses-native input types like `additional_tools`) and OpenRouter (404 on `/v1/responses` — endpoint
  not exposed) only serve `/v1/chat/completions`. `gpt-5.6-luna → grok-4.5` is still configured under
  `opencode-go-openai-responses` for future testing, but currently returns 422 from OpenCode Go and 404
  from OpenRouter — Codex requires full Responses API support which no provider reliably offers yet.
  `gpt-5.4`/`gpt-5.5` stay routed via `opencode-go-openai-chat` on `chat_completions` (proxy handles
  `/responses`→Chat downgrade). `AGENTS.md` updated with Migration Plans section documenting
  the provider dependency. `.conductor/scripts/imposter-container.sh` and the embedded `docker run` in
  `docs/wiki/setups/conductor.build-smooth-llm-imposter.md` — Docker now checks GHCR for a newer
  `smooth-llm-imposter:latest` on every container start. Related comments and the "Image pull behavior"
  note in the wiki doc updated to reflect the new pull behavior. OpenCode Go session forwarding switched
  from actively disabled (`OPENCODE_GO_{ANTHROPIC,OPENAI}_SESSION_FORWARDING=none` exports + `-e` flags)
  to using the image default (`SessionForwarding: opencode-go`) — the exports and `-e` flags are
  commented out with a note that they can be uncommented to stop OpenCode session token usage. Also
  added explicit `OpenAiUpstreamApi=chat_completions` override for `opencode-go-openai-chat` and
  `OpenAiUpstreamApi=responses` override for `opencode-go-openai-responses` to both scripts (image
  default is `responses`; the explicit overrides prevent accidental fallback to the wrong API for
  either provider). HLD 001 (`001-llm-imposter-routing/README.md`) JSON example was not changed — it
  still shows the legacy `opencode-go-openai` provider with `"chat_completions"` (stale vs. the live
  compound-provider split, but left in place). BaseUrl stays at `https://opencode.ai/zen/go` — the
  proxy forwarder already appends `/v1` in its endpoint paths.
  All 408 tests pass.
- 2026-07-30: `opencode-go-openai` switched from `chat_completions` to `responses` in
  `appsettings.json`, `appsettings.Development.json`, and the Conductor `imposter-container.sh` env overrides
  (the override line was removed). OpenCode Go's `zen/go` endpoint serves `/v1/responses` for the GPT-routed
  upstream targets (`kimi-k2.7-code`, `glm-5.2`, `grok-4.5`), so the `/responses`→Chat downgrade is no longer
  needed for those routes. This fixes the proxy-side 400 `Responses input item type 'additional_tools' cannot be
  downgraded to Chat Completions` on Codex `/responses` requests for `gpt-5.6-luna` (and similarly `gpt-5.4`/`gpt-5.5`):
  the proxy now forwards `/v1/responses` verbatim, `ToChatCompletions` never runs, and Responses-native input
  item types pass through untouched. `RequestNormalization` for `opencode-go-openai` now resolves to `None`
  (the `responses` default), which is correct — the Codex-to-OpenAI-SDK normalizer targets the Chat Completions
  tool contract and is forbidden on a `responses` provider by the validator. The `openrouter-openai` provider in
  `appsettings.Development.json` stays `chat_completions` since OpenRouter does not expose `/responses`. Verified
  end-to-end: upstream probed with `curl /v1/responses` (HTTP 200, real response for `grok-4.5`), local proxy
  reproduces the 400 on the old config and returns 200 on the new config, and `gpt-5.5` (`glm-5.2`) confirmed 200
  through the proxy. `setup.md` example updated to describe the new `opencode-go-openai` responses config.
- 2026-07-30: Added `.conductor/AGENTS.md`, the functional context file for the shared Conductor scripts
  described in the entry below — non-negotiables (single-source `docker run`, required `code-review-graph`
  flags, `run_mode`), setup/trigger instructions, the local-macOS-unverified caveat, and the
  `settings.local.toml` precedence gotcha (Conductor resolves settings per-value across layers per its own
  docs; a local `setup` value wins over this file's silently, so verify which one ran rather than assume).
- 2026-07-30: Added `.conductor/settings.toml` (committed, shared) so the workspace setup script — Docker
  container lifecycle plus the `code-review-graph` wiring documented below — reaches every teammate on clone
  instead of living only in each person's gitignored `.conductor/settings.local.toml`. The container `docker run`
  invocation lives once, in `.conductor/scripts/imposter-container.sh`; `setup.sh` (the `[scripts] setup`
  entrypoint) runs code-review-graph wiring then calls it, and `restart-imposter.sh` (the new
  `[scripts.run.restart-imposter]` on-demand trigger) calls it directly — recreates the container without
  rerunning code-review-graph or recreating the workspace, for a new image tag, a rotated
  `OPENCODE_API_KEY`/`OPENROUTER_API_KEY`, or a crash-looped container. `run_mode = "nonconcurrent"`: the
  container uses a fixed name and a fixed host port, so two workspaces racing setup/restart at once would
  collide. While extracting this, found the source `settings.local.toml` defined `opencode-go-anthropic`
  provider index 2 twice (`claude-opus-4-7→minimax-m3`, then a differently-indented `claude-opus-4-8→qwen3.7-max`)
  — Docker's env map keeps the later `-e` for a repeated name, so `claude-opus-4-7` was silently unrouted.
  Confirmed with the user: `opus-4-8→qwen3.7-max` is intentional; `opus-4-7` is dropped, not added as a third
  index. Updated the mapping table and embedded script in the wiki doc to match. Verified end-to-end: `bash -n`
  on all three new scripts, `.conductor/settings.toml` validated against the real
  `settings.repo.schema.json` (via `jsonschema`), and `setup.sh` run for real in this sandbox — code-review-graph
  installed on all four platforms (215 files / 1326 nodes / 6861 edges), and the container came up healthy on
  `127.0.0.1:5080`. That run used this sandbox's real credentials and Docker daemon (inherited from the shell
  environment, not scoped to a throwaway clone) and recreated the workspace's already-running
  `smooth-llm-imposter` container — the intended restart behavior, not an isolated test, but worth knowing since
  it mutated live local state rather than a disposable one. The shared `imposter-container.sh` also actively
  disables session forwarding per-provider (previously the manual-paste script kept these commented out); the
  wiki setup doc and this changelog were updated to match, and the matching `-e` flags were added to `docker run`.
- 2026-07-30: Dockerfile switched both stages from Debian to `-alpine` .NET 10 base images (`sdk:10.0-alpine`, `aspnet:10.0-alpine`). Image size dropped from ~240 MB to ~131 MB (~45% reduction). No trimming, self-contained, or GC-mode changes — base-image swap only. Alpine ships `wget` (no `curl`), so the Dockerfile's HEALTHCHECK comment was updated accordingly. Verified with smoke tests.
- 2026-07-30: Added `.code-review-graph/` to `.gitignore` and created `.github/instructions/code-review-graph.instructions.md` for Copilot MCP tool usage.
- 2026-07-30: Conductor workspace setup wires `code-review-graph` into two more platforms — `opencode` and `claude-code` — alongside the existing `codex` and `copilot-cli`. `claude-code` was previously excluded for mutating tracked files; `--no-skills --no-hooks` removes that (its skills/hooks otherwise resolve through the committed `.claude -> .agents` symlink into `.agents/skills/` and `.agents/settings.json`). `--no-instructions` is now passed to **all four**: by default `install` appends a ~39-line section to `CLAUDE.md`, which is a committed symlink to `AGENTS.md`, so the append lands in the root context file — dormant today only because the lifecycle has no TTY. The two project-scoped configs (`.mcp.json`, `opencode.jsonc`) are added to `.git/info/exclude` rather than `.gitignore`, keeping workspace diffs clean without touching a tracked file. Known residue: `install` still appends `.code-review-graph/` to `.gitignore` because it greps that file instead of consulting `git check-ignore`. Verified in a throwaway clone: all four configured, `AGENTS.md`/`.agents/settings.json` untouched, graph built at 215 files / 1326 nodes / 6861 edges.
- 2026-07-30: Conductor snapshot setup installs `uv` (astral.sh) alongside the other CLI tooling in step [2]. The installer is piped into `env UV_NO_MODIFY_PATH=1 sh` rather than bare `sh`: the variable must be set on the *right* side of the pipe, since a `VAR=x curl ... | sh` prefix only reaches `curl`. Without it the installer defaults to `NO_MODIFY_PATH=0` and appends its own PATH line to `.bashrc`/`.zshrc`, competing with step [3], which is the single owner of those files. Verified on AL2023: installs `uv` + `uvx` to `~/.local/bin` (already covered by the step [3] PATH exports) with all shell rc files unchanged. This is a functional fix, not a convenience: every `code-review-graph install` writes an MCP config whose command is `uvx code-review-graph serve`, for all platforms, regardless of how the tool itself was installed. The snapshot's venv + `/usr/local/bin` symlink only satisfies `build`, so before this change the MCP server could not start while `build` still reported success — a graph no agent could query. Confirmed post-change with a stdio `initialize` handshake against `uvx code-review-graph serve` (server v3.4.5 responded).
- 2026-07-29: Conductor workspace setup no longer overrides OpenCode Go session forwarding — it uses the image default (`SessionForwarding: opencode-go`), so matched routes stamp `session_id` / `x-opencode-session`. The per-provider opt-out ships commented out; re-enabling it requires the two exports, both names in `--preserve-env`, and the two `-e` flags. The `-e` flags are documented above the `docker run` rather than inline, because a `#` comment inside a backslash-continued argument list comments out every remaining line.
- 2026-07-29: Conductor setup adds GitHub Copilot CLI and `code-review-graph`. The snapshot installs Python 3.12 + a dedicated venv (a `pip install --user` silently yields a 0-node graph — grammar probes run under `python -I`, which drops user site-packages); the workspace runs the repo-scoped `install --platform codex|copilot-cli` and `build`. `claude-code` is skipped because it rewrites tracked files (`CLAUDE.md`, `.claude/skills`, `.agents/settings.json`).
- 2026-07-25: Conductor build setup removes `OpenAiUpstreamApi=chat_completions` from `opencode-go-openai` — GPT routes now use the Responses API default (no `/responses`→Chat downgrade). **Superseded 2026-07-29** — the image's `appsettings.json` already ships that value, so the change was a redundant-override cleanup, not an API switch; GPT-route behavior (`/v1/chat/completions`) is unchanged.
- 2026-07-29: Conductor build setup correction — the 2026-07-25 entry above was misleading; the `OpenAiUpstreamApi=chat_completions` removal was a redundant-override cleanup (the image default already provides it), not an API switch to Responses. See `src/SmoothLlmImposter.Host/appsettings.json` and `src/SmoothLlmImposter.Application/Features/Routing/AGENTS.md`.
- 2026-07-25: HLD 010 `--who?` and `--newsession` switch family implemented — `--who?` short-circuits the forward path with a dialect-shaped synthetic reply naming the inbound model, resolved upstream (or `passthrough`), auth scheme, and session identity; `--newsession` mints a synthetic session id and stores a caller→synthetic mapping in the `ISessionTranslationDictionary` singleton. Forward-path translation seam added. Diagnostic logging added to `WhoMessageResponder` (non-match reasons) and `RoutingEndpoints` (feature-disabled, translation applied). **Breaking change:** Bare `who?` trigger was replaced by `--who?`. LADRs/NFRs → Accepted; HLD → Completed.
- 2026-07-25: Conductor workspace setup disables OpenCode Go session forwarding (`OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING=none` and `OPENCODE_GO_OPENAI_SESSION_FORWARDING=none`). **Superseded 2026-07-29** — the overrides now ship commented out.
- 2026-07-25: Conductor workspace setup mappings — sonnet/opus-4-6/opus-4-7→opencode-go (qwen3.6-plus/qwen3.7-plus/minimax-m3), haiku→OpenRouter tencent/hy3, gpt-5.4/5.5/5.6-luna→opencode-go (kimi-k2.7-code/glm-5.2/grok-4.5); require OPENROUTER_API_KEY; `openrouter-anthropic` defined fully in script (absent from base image).
- 2026-07-24: Note Conductor sandbox Docker CLI + Compose v2 setup-doc guidance.
- 2026-07-24: Document credential-independent Conductor snapshot preparation and credential-aware workspace container startup.
- 2026-07-30: `publish-image.yml` manual `workflow_dispatch` no longer emits the `:latest` tag — it now publishes a required `version` input (e.g. `1.0.0-rc.1`) as a pre-release tag instead, while `:latest` is reserved for automatic `push` to `main`. Manual dispatch remains available from any branch, so pre-release images can be cut from non-main branches.
