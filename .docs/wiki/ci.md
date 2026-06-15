# CI/CD

The pipeline is a single PR gate that builds and tests every change before it can merge to `main`.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.

### Service container

The `build-and-test` job declares a single GitHub Actions service container — **WireMock** (`wiremock/wiremock`), published on `127.0.0.1:19091` → container `8080`. It is the only external dependency: this router is stateless and key-less, so there is **no PostgreSQL, Redis, or Aspire host**. The current test suite stubs the upstream transport in-process; the WireMock service is provisioned for integration tests that stub real upstream LLM endpoints over HTTP.

### Steps

1. **Checkout** — `actions/checkout@v4`.
2. **Install .NET SDK** — `actions/setup-dotnet@v4` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
3. **Restore** — `dotnet restore SmoothLlmImposter.slnx`.
4. **Build** — `dotnet build SmoothLlmImposter.slnx --no-restore --configuration Release`.
5. **Test with coverage** — local action `.github/actions/test-with-coverage`:
   - Waits for the WireMock service health endpoint (`http://127.0.0.1:19091/__admin/health`) before running tests.
   - Restores .NET tools (`dotnet tool restore`).
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
   - Runs the whole solution in one pass (`dotnet test SmoothLlmImposter.slnx`) with Cobertura coverage scoped to `[SmoothLlmImposter.*]*`.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
6. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
7. **Upload coverage artifacts** (`if: always()`) — uploads `artifacts/coverage/` as `coverage-report`.

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.
