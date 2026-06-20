# CI/CD

Two workflows: a **PR gate** that builds and tests every change before it can merge to `main`, and a **publish-image** workflow that builds and pushes the Host container image to GHCR from `main` and version tags.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.

### Jobs

The workflow is three jobs so the gate always reports a result even when a PR touches no buildable files (a `paths:` trigger would silently skip and leave a required check pending forever):

1. **`changes`** — checks out with `fetch-depth: 0` and decides whether the build is needed. For `push` and `workflow_dispatch` it always returns `src=true`; for `pull_request` it runs `git diff base..head` and matches `^(\.config/|\.github/|Directory\.Packages\.props|SmoothLlmImposter\.slnx|src/|tests/)`.
2. **`build-and-test`** — runs only when `changes.outputs.src == 'true'`. Restore → build → test with coverage (steps below).
3. **`ci-gate`** — runs `if: always()` and is the single check intended to be marked required on `main`. It passes when `build-and-test` is `success` **or** `skipped`, and fails otherwise.

### Service container

The `build-and-test` job declares a single GitHub Actions service container — **WireMock** (`wiremock/wiremock`), published on `127.0.0.1:19091` → container `8080`. It is the only external dependency: this router is stateless and key-less, so there is **no PostgreSQL, Redis, or Aspire host**. The current test suite stubs the upstream transport in-process; the WireMock service is provisioned for integration tests that stub real upstream LLM endpoints over HTTP.

### Steps

1. **Checkout** — `actions/checkout@v6`.
2. **Install .NET SDK** — `actions/setup-dotnet@v5` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
3. **Restore** — `dotnet restore SmoothLlmImposter.slnx`.
4. **Build** — `dotnet build SmoothLlmImposter.slnx --no-restore --configuration Release`.
5. **Test with coverage** — local action `.github/actions/test-with-coverage`:
   - Waits for the WireMock service health endpoint (`http://127.0.0.1:19091/__admin/health`) before running tests.
   - Restores .NET tools (`dotnet tool restore`).
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
   - Runs the whole solution in one pass (`dotnet test SmoothLlmImposter.slnx`) with Cobertura coverage scoped to `[SmoothLlmImposter.*]*`.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
6. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
7. **Upload coverage artifacts** (`if: always()`) — `actions/upload-artifact@v7`, uploads `artifacts/coverage/` as `coverage-report`.

## PR Evals Gate (L3)

- **Workflow:** `.github/workflows/pr-evals-gate.yml`
- **Triggers:** `pull_request` → `main` (paths `src/**`, `tests/SmoothLlmImposter.Upstream.EvalTest/**`, the workflow file) and manual `workflow_dispatch`.
- **Purpose:** runs the **L3** live-upstream conformance evals (HLD 004 LADR-04 / NFR-04) — the only tier that calls a real OpenAI-compatible upstream (`opencode-go`/kimi). It is **deliberately separate** from the hermetic `pr-gate` above: the eval project is excluded from `SmoothLlmImposter.slnx`, and this workflow targets `tests/SmoothLlmImposter.Upstream.EvalTest` directly.
- **Secret:** `OPENCODE_API_KEY` (org secret), passed via job `env` from `secrets.OPENCODE_API_KEY`. Fork PRs receive no secrets, so the value is empty there and every eval **skips** — the job stays green (**neutral**). The key is never echoed; the only step that references it tests presence with `-n` and prints a yes/no summary line.
- **Status:** **non-blocking initially** — do **not** register `upstream-evals` as a required status check on `main` yet. Promote it to required only once the eval matrix is trusted.

## Publish image

- **Workflow:** `.github/workflows/publish-image.yml`
- **Triggers:** `push` → `main` (tagged `:latest`), `push` tags `v*` (semver tags), and manual `workflow_dispatch`.
- **Permissions:** `contents: read`, `packages: write` (pushes to GHCR with the job's `GITHUB_TOKEN`).
- **Image:** `ghcr.io/<owner>/smooth-llm-imposter` (from `github.repository`), built from the repo `Dockerfile` (multi-stage, non-root, listens on `5080`).
- **Tags** (via `docker/metadata-action`): `latest` on the default branch, `{{version}}` + `{{major}}.{{minor}}` on `v*` tags, and a short-`sha` tag for traceability.
- **Caching:** GitHub Actions build cache (`type=gha`, `mode=max`).
- **One-time:** the first push creates a **private** package — an org owner sets it public (or consumers `docker login ghcr.io`). See [`setups/ghcr.run-smooth-llm-imposter.md`](setups/ghcr.run-smooth-llm-imposter.md).

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.
