# CI/CD

The pipeline is a single PR gate that builds and tests every change before it can merge to `main`.

## PR Gate

- **Workflow:** `.github/workflows/pr-gate.yml`
- **Triggers:** `pull_request` → `main` (including PR branch updates), `push` → `main`, and manual `workflow_dispatch`.

### Steps

1. **Checkout** — `actions/checkout@v4`.
2. **Install .NET SDK** — `actions/setup-dotnet@v4` (version from the `DOTNET_VERSION` env, currently `10.0.x`).
3. **Restore** — `dotnet restore Project.slnx`.
4. **Build** — `dotnet build --no-restore --configuration Release`.
5. **Aspire test with coverage** — local action `.github/actions/aspire-test-with-coverage`:
   - Starts `tests/Project.TestFramework.Aspire`, keeps its PID inside the action script, and waits for PostgreSQL (`127.0.0.1:15432`), Redis (`127.0.0.1:16379`), and WireMock (`http://127.0.0.1:19091/__admin/health`).
   - Restores .NET tools (`dotnet tool restore`) after the dependency pre-warm, matching the proven CI timing before tests start.
   - Prepares `artifacts/testresults/` and `artifacts/coverage/`.
   - Runs test projects in order: Host integration → Application/Infrastructure component → Domain/Application/Infrastructure/Host unit tests.
   - Generates coverage reports with `dotnet tool run reportgenerator`.
   - Stops the Aspire host from the action script's teardown trap once tests and coverage have finished or failed.
6. **Publish coverage summary** (`if: always()`) — appends `artifacts/coverage/SummaryGithub.md` to the GitHub step summary.
7. **Upload coverage artifacts** (`if: always()`) — uploads `artifacts/coverage/` as `coverage-report`.

## .NET local tools

`.config/dotnet-tools.json` declares the local tool manifest, restored in CI (and locally) with `dotnet tool restore`:

| Tool | Version | Command |
|---|---|---|
| `dotnet-reportgenerator-globaltool` | `5.4.4` | `reportgenerator` |
| `dotnet-ef` | `10.0.8` | `dotnet-ef` |

`dotnet-ef` is pinned to the EF Core runtime version (`Directory.Packages.props`) so the migrations CLI never drifts from the `Microsoft.EntityFrameworkCore.*` packages. Bump both together.
