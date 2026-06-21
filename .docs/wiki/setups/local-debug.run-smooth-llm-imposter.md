# Local debug → run SmoothLlmImposter from source with a debugger

## TL;DR

Run the Host directly on your machine for **debugging the codebase** — breakpoints, hot edits, the Scalar/OpenAPI
UI — and keep upstream keys out of the repo using **.NET user secrets** (a.k.a. dev secrets). The Host listens on
`http://localhost:5080` (and `https://localhost:7080`) under its launch profile. Routing is driven by the
**`Imposter`** config section.

## Prerequisites

- **.NET 10 SDK** on PATH (`dotnet --list-sdks` shows a `10.*`).
- An IDE/debugger if you want breakpoints: Visual Studio, JetBrains Rider, or VS Code with the C# Dev Kit. A plain
  `dotnet run` works without one.

## Run with the debugger

The repo ships two launch profiles (`src/SmoothLlmImposter.Host/Properties/launchSettings.json`), both setting
`ASPNETCORE_ENVIRONMENT=Development`:

| Profile | URL(s) |
|---|---|
| `http`  | `http://localhost:5080` |
| `https` | `https://localhost:7080` + `http://localhost:5080` |

```bash
# CLI
dotnet run --project src/SmoothLlmImposter.Host                      # http profile -> :5080
dotnet run --project src/SmoothLlmImposter.Host --launch-profile https
```

- **Visual Studio / Rider** — open `SmoothLlmImposter.slnx`, set `SmoothLlmImposter.Host` as the startup project,
  pick the `http`/`https` profile, and press **F5**.
- **VS Code** — open the repo with the C# Dev Kit; run/debug the `SmoothLlmImposter.Host` target. It honours the
  same launch profiles.

In `Development` you also get the API docs:

| Interface | URL |
|---|---|
| Scalar API docs | `http://localhost:5080/scalar/v1` |
| OpenAPI schema  | `http://localhost:5080/openapi/v1.json` |

## Supply keys via .NET user secrets (dev secrets)

The Host already declares `<UserSecretsId>smoothllmimposter-host</UserSecretsId>`, so `dotnet user-secrets` works
out of the box — **in the `Development` environment only**. Secrets are stored **outside the repo**
(`~/.microsoft/usersecrets/smoothllmimposter-host/secrets.json` on Linux/macOS;
`%APPDATA%\Microsoft\UserSecrets\…` on Windows), so they can never be committed.

User-secret keys use the **`:` path separator** (JSON config path), not the `__` form used for environment
variables:

```bash
cd src/SmoothLlmImposter.Host

# Upstream provider keys (keyed by provider name, order-independent)
# AuthScheme (ApiKey|Bearer) selects the header; defaults by dialect (openai -> Bearer, anthropic -> ApiKey).
dotnet user-secrets set "Imposter:Providers:opencode-go-openai:Secret" "sk-your-opencode-key"
dotnet user-secrets set "Imposter:Providers:opencode-go-anthropic:Secret" "sk-your-anthropic-route-key"
dotnet user-secrets set "Imposter:Providers:openrouter-anthropic:Secret" "sk-your-openrouter-key"
dotnet user-secrets set "Imposter:Providers:openrouter-anthropic:AuthScheme" "Bearer"

# Optional: credential-admin API (also needs a PostgreSQL connection string).
# Unset -> no database (NullCredentialStore); see setups/credentials.admin-smooth-llm-imposter.md
dotnet user-secrets set "Admin:ApiKey" "choose-a-strong-admin-key"
dotnet user-secrets set "ConnectionStrings:ImposterDb" "Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres"

dotnet user-secrets list        # verify (prints values — run in a private shell)
```

Then just `dotnet run` / F5 — no `export` needed for the keys.

### Precedence (which value wins)

Configuration providers are layered; **later wins**:

```
appsettings.json  <  appsettings.Development.json  <  user secrets (Dev only)  <  environment variables  <  CLI args
```

So an **environment variable overrides a user secret** of the same key. Use user secrets as your stable local
default and an `export OPENCODE_GO_API_KEY=…` (conventional) or `export Imposter__Providers__opencode-go-openai__Secret=…`
(structured) to override opencode-go-openai ad hoc for a single run. (User secrets are
**not** loaded outside `Development` — production/staging must use environment variables or another secure
provider.)

## Verify

```bash
curl -fsS http://localhost:5080/health        # {"status":"ok"}

curl -fsS http://localhost:5080/v1/chat/completions \
  -H "content-type: application/json" \
  -d '{ "model": "gpt-5.4", "messages": [ { "role": "user", "content": "Say hello in one sentence." } ] }'
```

Set a breakpoint in `ImposterRouter` (model resolution) or `UpstreamForwarder` (outbound request) to watch the
rewrite-and-forward happen.
