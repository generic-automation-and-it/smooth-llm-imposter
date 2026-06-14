# Project

> One-line description of what the service does and who it is for. _(Placeholder — update once the project is named.)_

`Project` is a **combined AI DevEx template** — a starting point for teams that want structured, tool-agnostic AI-assisted development from day one. It ships a ready-to-use AI agent toolchain (Claude Code, Cursor, GitHub Copilot, OpenAI Codex) wired up via a single `.agents/` directory, alongside a **.NET 10 / ASP.NET Core** reference implementation built with **Clean Architecture**.

> **⚠️ Template repository.** `Project` is a placeholder name used throughout the solution (`Project.slnx`, `src/Project.*`, `tests/Project.*`), the `.agents` tree, and this README. When the project is given a real name, rename every `Project`/`project` occurrence and update the descriptions below. See the Template Notice in [`AGENTS.md`](AGENTS.md) for the full checklist.

---

## Tech Stack

### AI Toolchain

| Component | Technology |
|---|---|
| Agent scaffold | `.agents/` — single source of truth for all AI tools |
| Coding agents | Claude Code · GitHub Copilot · Cursor · OpenAI Codex |
| Skills | Executable multi-file workflows in `.agents/skills/` |
| Rules | Per-file coding standards in `.agents/rules/` |
| Prompts & roles | Reusable prompt templates and multi-agent role instructions |
| Hooks | `PostToolUse` / `UserPromptSubmit` automation via `.agents/hooks/` |

### .NET Reference Implementation

| Component | Technology |
|---|---|
| Framework | ASP.NET Core (.NET 10) |
| Architecture | Clean Architecture — `Domain` / `Application` / `Infrastructure` / `Host` |
| API style | Minimal API endpoints (`src/Project.Host`) |
| Mediator | [`martinothamar/Mediator`](https://github.com/martinothamar/Mediator) — source-gen CQRS dispatch |
| Validation | FluentValidation in a fail-fast Mediator pipeline |
| Persistence | EF Core + PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Observability | Serilog + OpenTelemetry, Scalar OpenAPI UI |
| Testing | xunit.v3 · Shouldly · Bogus · Respawn |

---

## Getting Started

### Prerequisites

- **.NET 10 SDK**
- A container runtime — Docker Desktop, Rancher Desktop, Colima, or Podman (for PostgreSQL via Aspire)

### One-time AI-agent setup

The repository drives four AI coding agents from a single `.agents/` directory via symlinks (`.claude`, `.codex`, `.cursor` → `.agents`, and `CLAUDE.md`/`GEMINI.md` → `AGENTS.md`). Run the setup script once after cloning so the agents can discover skills, hooks, and rules:

```bash
# Mac/Linux
bash .agents/setup/scripts/agents-setup.sh
```

```powershell
# Windows (requires admin; enable Developer Mode for symlink support)
powershell -ExecutionPolicy Bypass -File .agents/setup/scripts/agents-setup.ps1
```

> On Windows, enable Developer Mode (**Settings → System → For developers → Developer Mode**) so symlinks resolve.

### Build & Test

```bash
dotnet restore Project.slnx
dotnet build   Project.slnx --configuration Release
dotnet test    Project.slnx
```

Target a single test project directly when iterating, e.g. `dotnet test tests/Project.Domain.UnitTest`.

### Run locally

```bash
dotnet run --project src/Project.Host      # start the API
```

Once the stack is up:

| Interface | URL |
|---|---|
| Scalar API Docs | `/scalar/v1` on the Host |
| OpenAPI schema | `/openapi/v1.json` on the Host |

---

## Project Structure

```
.agents/                         # All AI tooling — single source of truth
  hooks/                         # PostToolUse / UserPromptSubmit automation
  prompts/                       # Reusable prompt templates
  roles/                         # Multi-agent role instructions (PO, Architect, QA, …)
  rules/                         # Per-file coding standards (auto-loaded by agents)
  skills/                        # Executable multi-file workflows
  setup/                         # One-time symlink / config setup scripts
  settings.json                  # Tool permissions, compile/test commands

src/
  Project.Domain/          # Entities, value objects, invariants — no external deps
  Project.Application/     # Vertical-slice use cases (Features/<Name>/) + Mediator handlers
  Project.Infrastructure/  # EF Core + PostgreSQL persistence, HTTP clients
  Project.Host/            # Minimal API composition, middleware, observability

tests/
  Project.*.UnitTest/          # L0 — no I/O, in-process
  Project.*.ComponentTest/     # L1 — in-memory EF Core / real isolated DB + Respawn
  Project.*.IntegrationTest/   # L2 — full stack, real PostgreSQL
  Project.TestFramework/       # Shared fixtures
  Project.TestFramework.Aspire/# Aspire dependency host (PostgreSQL + WireMock)
```

---

## Documentation

| Topic | Location |
|---|---|
| AI agent context & coding rules | [`AGENTS.md`](AGENTS.md) · [`.agents/`](.agents/) |
| Architecture & design | [`.docs/wiki/architecture.md`](.docs/wiki/architecture.md) |
| AI tooling setup | [`.docs/wiki/ai-tooling.md`](.docs/wiki/ai-tooling.md) |
| Testing strategy | [`.docs/wiki/testing.md`](.docs/wiki/testing.md) |
| CI/CD pipeline | [`.docs/wiki/ci.md`](.docs/wiki/ci.md) |
| Architecture decisions & NFRs | [`.docs/adr/`](.docs/adr/) · [`.docs/nfr/`](.docs/nfr/) |

---

## Contributing

- Work on a branch off `main`: `<type>/<ticket>-short-description` (e.g. `feat/1234-add-user-export`).
- Commits and PR titles follow [Conventional Commits](https://www.conventionalcommits.org). See [`.agents/rules/git/`](.agents/rules/git/).
- Every PR should create or update at least one `*_AGENTS.md` context file.
