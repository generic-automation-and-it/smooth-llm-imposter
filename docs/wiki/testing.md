# Testing Strategy

This router is **stateless and key-less** — no database, no message broker. Tests need no Docker or DB; the
only external thing worth faking is the upstream LLM provider's HTTP call.

## Test Levels

| Level | Label | Projects | Containers? | Description |
|---|---|---|---|---|
| L0 | Unit | `SmoothLlmImposter.{Domain,Application,Infrastructure,Host}.UnitTest` | None | Isolated logic, no I/O — pure in-process |
| L2 | Integration | `SmoothLlmImposter.Host.IntegrationTest` | None by default; optional WireMock | Boots the real Host via `WebApplicationFactory` and stubs the upstream transport |
| L3 | Live upstream eval | `SmoothLlmImposter.Upstream.EvalTest` | None — calls the **real** upstream over the internet | Conformance evals against a live OpenAI-compatible upstream (e.g. `opencode-go`/kimi). Secret-gated and **neutral (skipped)** when the secret is absent |

There is no L1 "component" tier and no PostgreSQL/Redis — those were removed along with the persistence the
template assumed.

### L3 — live upstream evals (HLD 004 LADR-04 / NFR-04)

L0 and L2 are **hermetic**: no external network, no secrets. L3 is the deliberate exception — it verifies
that what the proxy sends is actually accepted by a strict, real upstream (the thing only a live call can
prove). It is kept strictly separate from the hermetic tiers:

- **Excluded from `SmoothLlmImposter.slnx`.** `dotnet test SmoothLlmImposter.slnx` (the pr-gate command)
  never runs it. It is built/run only by targeting its project directly.
- **Secret-gated and neutral by default.** Every test reads `OPENCODE_API_KEY` from the environment and
  calls `Assert.Skip(...)` when it is absent — so the tier is a green no-op on fork PRs and on any machine
  without the key. The key is never logged.
- **Driven through the real transform.** The eval runs a raw Codex tool catalog through the genuine
  `OpenAiRequestTransformer` + `CodexToOpenAiSdkNormalizer`, sends the result to the live upstream, and
  asserts `200`; a companion case asserts the un-normalized catalog still returns `400` (the upstream
  contract is unchanged, so normalization is genuinely required).

Run it locally with the key set:

```bash
OPENCODE_API_KEY=sk-... dotnet test tests/SmoothLlmImposter.Upstream.EvalTest
```

In CI it runs in its own workflow (`.github/workflows/pr-evals-gate.yml`), separate from the hermetic
`pr-gate` and **non-blocking** initially. See [`ci.md`](ci.md#pr-evals-gate-l3).

## Test Infrastructure

Shared fixtures live in `tests/SmoothLlmImposter.TestFramework/` (a library; `IsTestProject=false`, no
tests). See `tests/SmoothLlmImposter.TestFramework/TEST_FRAMEWORK_AGENTS.md` for the authoritative list. Key
pieces:

- **`WebAppFixture<TProgram>`** — wraps `WebApplicationFactory<TProgram>`. Integration tests reference the
  Host with `Aliases="HostApp"` and close it as `WebAppFixture<HostApp::Program>` to avoid an ambiguous
  `Program` (xunit.v3 compiles each test assembly as an executable with its own `Program`).
- **`ServiceProviderFixture`** — builds an isolated `IServiceProvider` for L0 tests and routes logging to
  xunit output via `XUnitLoggerFactory`.
- **`PriorityOrderer` + `[TestPriority]`** — order test cases when sequencing matters.

## Stubbing the upstream

L2 tests fake the upstream LLM provider in one of two ways:

1. **In-process stub transport (default).** `ImposterAppFixture` swaps the named `imposter-upstream`
   `HttpClient`'s primary handler for `StubUpstreamHandler`, which captures the outbound request (URI, auth
   headers, transformed body) and returns a canned response. Best for asserting *what the forwarder sent
   upstream*. Routing config is supplied per-test (in-memory config or `appsettings`).
2. **WireMock service container (CI).** The PR gate runs `wiremock/wiremock` on `127.0.0.1:19091` for tests
   that need a real HTTP endpoint (streaming/SSE passthrough, transport-failure → 502). Point a provider's
   `BaseUrl` at the WireMock host and program stubs through its admin API.

See `.agents/rules/backend/wiremock-stubbing.instructions.md` for the stubbing conventions.

## Running Tests

```bash
# All tests (L0 + L2) — no Docker required
dotnet test SmoothLlmImposter.slnx

# A single tier / project
dotnet test tests/SmoothLlmImposter.Domain.UnitTest
dotnet test tests/SmoothLlmImposter.Application.UnitTest
dotnet test tests/SmoothLlmImposter.Infrastructure.UnitTest
dotnet test tests/SmoothLlmImposter.Host.UnitTest
dotnet test tests/SmoothLlmImposter.Host.IntegrationTest
```

For the WireMock-backed integration path locally, start the stub before running those tests:

```bash
docker run --rm -p 19091:8080 wiremock/wiremock:3.13.1
```
