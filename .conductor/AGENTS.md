# AGENTS.md - Conductor Workspace Scripts

AI Context: shared Conductor repository scripts (`.conductor/settings.toml` + `.conductor/scripts/`) that
bring up the SmoothLlmImposter Docker container and wire `code-review-graph` on every teammate's workspace.
Human-facing walkthrough and the manual UI paste-in alternative: `.docs/wiki/setups/conductor.build-smooth-llm-imposter.md`.
Updated: 2026-07-30

## TL;DR

`.conductor/settings.toml` is committed and shared, so `git pull` is enough to get it — unlike
`.conductor/settings.local.toml`, which is machine-local and never reaches teammates. It points Conductor's
`[scripts] setup` (runs once, on workspace creation) and `[scripts.run.restart-imposter]` (on-demand trigger)
at scripts under `.conductor/scripts/`, which start the imposter container and, on setup only, wire
`code-review-graph` into four AI-coding platforms.

## Non-Negotiables

- **The `docker run` invocation lives in exactly one place: `imposter-container.sh`.** `setup.sh` and
  `restart-imposter.sh` both call it; neither may embed its own copy of the provider `-e` flags. The provider
  index bug this replaced (`opencode-go-anthropic` index `2` defined twice, silently dropping
  `claude-opus-4-7`) happened because the script existed as one long copy-pasted string with no single source
  of truth — see Changelog.
- **`run_mode` must stay `nonconcurrent`** (`.conductor/settings.toml`). The container uses a fixed name
  (`smooth-llm-imposter`) and a fixed host port (`127.0.0.1:5080` by default via `PORT`); two workspaces
  running `setup` or `restart-imposter` at the same time would race the same `docker rm -f` / `docker run`.
- **Every `code-review-graph install --platform` call in `setup.sh` keeps `--no-instructions`.** Without it,
  `install` appends an MCP-tools section to `CLAUDE.md`, which in this repository is a committed symlink to
  root `AGENTS.md` — the append would land in the tracked context file every teammate's agent reads.
- **The `claude-code` platform additionally keeps `--no-skills --no-hooks`.** Its skills/hooks resolve through
  the committed `.claude -> .agents` symlink into `.agents/skills/` (81 tracked files) and
  `.agents/settings.json`; the other three platforms write under `$HOME` instead and don't need the flags.
- **Never hardcode `OPENCODE_API_KEY` / `OPENROUTER_API_KEY` here.** `imposter-container.sh` reads them from
  the workspace environment and fails fast (`:?`) if either is unset — that's Conductor's job to supply, not
  this script's.

## Architecture Decisions

| Decision | Rejected alternative | Why |
|---|---|---|
| Extract the container lifecycle into its own file (`imposter-container.sh`), called by both `setup.sh` and `restart-imposter.sh` | Inline the `docker run` separately in each of `[scripts] setup` and `[scripts.run.restart-imposter]` as TOML strings, matching Conductor's own simple `pnpm install`-style example | Two copies of ~30 `-e` flags drift silently — this is exactly how the `opencode-go-anthropic` index-2 collision happened in the personal script this replaced. |
| Commit `.conductor/settings.toml` + `.conductor/scripts/*.sh` | Keep the workspace script only in `.conductor/settings.local.toml` (as it existed before this change) | `settings.local.toml` is machine-local; every teammate had to hand-paste the script into their own workspace to get it at all. |
| Keep `code-review-graph install --platform claude-code` (flag-gated) rather than excluding the platform entirely | Skip `claude-code` outright, as the original wiki doc did, because default `install` mutates tracked files | `--no-instructions --no-skills --no-hooks` gets the same safety (only writes untracked `.mcp.json`) without losing the fourth platform's code-intelligence coverage. |

## Key Behaviors

- **Session forwarding opt-out** — `imposter-container.sh` defaults both
  `OPENCODE_GO_{ANTHROPIC,OPENAI}_SESSION_FORWARDING` to `none` in the host shell, preserves them through
  `sudo --preserve-env`, and passes the matching `-e` flags to `docker run` so the container sees them.
  Matched routes therefore do **not** stamp `session_id` / `x-opencode-session`. To re-enable, comment out
  both exports, remove both names from `--preserve-env`, and remove the two `-e` flags.
- **Enabling this in a new workspace.** Nothing to configure beyond secrets: once
  `.conductor/settings.toml` is on the branch a workspace is created from, Conductor runs its `setup` script
  automatically. The only prerequisite is that the workspace has `OPENCODE_API_KEY` and `OPENROUTER_API_KEY`
  set as environment variables (Conductor workspace/environment settings, not committed anywhere) — without
  them `imposter-container.sh` exits immediately with a `:?` message naming the missing variable.
- **Running the trigger.** `restart-imposter` shows up as a named, on-demand run script (icon
  `refresh-cw`) in Conductor — run it any time to recreate the container without recreating the workspace:
  after pulling a new image tag, rotating either API key, or recovering from a crash-looped container. It
  skips the `code-review-graph` step entirely (only `setup.sh` runs that).
- **Precedence gotcha.** Conductor resolves settings per-value across layers — "if two layers set the same
  value, Conductor uses the highest layer that applies" (repository local outranks repository shared). The
  docs don't specify the merge granularity below that (e.g. whether a local `[scripts] setup` masks only that
  key or the whole `[scripts]` table). Practically: a teammate who already has a personal
  `settings.local.toml` with its own `setup` value will keep running that instead of this file's `setup.sh`,
  silently, until they delete or reconcile it — verify which one actually ran (check for the
  code-review-graph install log lines, only present via this file's `setup.sh`) rather than assuming.
- **Cloud-sandbox assumptions, not verified on local macOS.** `imposter-container.sh` starts `dockerd`
  directly with `sudo nohup` when `docker info` fails, matching the Amazon Linux 2023 cloud sandbox lifecycle
  (no systemd as PID 1) documented in the wiki setup doc. It has only been run in that cloud sandbox. A local
  Mac workspace normally has Docker Desktop already running its own daemon on a different socket path than the
  hardcoded `unix:///var/run/docker.sock` default, and `--pull=never` assumes the image was already pulled by
  the (cloud-only) snapshot script. `[scripts] setup` has no `available_in` gate — unlike `[scripts.run.*]` —
  so it runs unconditionally on every workspace, local or cloud. Until this is verified working on macOS,
  local users who hit failures should override `setup` via a personal `settings.local.toml` (see the
  precedence gotcha above).
- **Idempotent recreate, not incremental update.** Every run (`setup` or `restart-imposter`) does
  `docker rm -f` then `docker run -d` unconditionally — there's no update-in-place path, no volumes to lose,
  and no state carried between recreations beyond what's baked into the image + the `-e` flags.
- **The MCP servers `code-review-graph install` configures all invoke `uvx code-review-graph serve`**,
  regardless of platform. If a workspace's snapshot doesn't have `uv` on `PATH`, the generated MCP configs are
  written successfully but the servers themselves cannot start — `code-review-graph build` still exits 0 in
  that case, so the graph looks built but no agent can reach it over MCP.
- **Project-scoped MCP configs are hidden via `.git/info/exclude`, not `.gitignore`.** `setup.sh` seeds
  `.mcp.json` (from `claude-code`) and `opencode.jsonc` (from `opencode`) into `.git/info/exclude` so they
  never show up in a workspace diff, without editing the tracked `.gitignore`. `code-review-graph install`
  still appends `.code-review-graph/` to the tracked `.gitignore` directly (all platforms, predates this
  script) — that one line is expected to show up as a diff in every workspace.

## Migration Plans

Any teammate's pre-existing `.conductor/settings.local.toml` that duplicates this shared script's container
logic should be deleted once its behavior is confirmed equivalent — per the precedence gotcha above, a
lingering local file silently masks every update made here. There's no forcing function for this; it's a
manual cleanup step per teammate, not something this script can detect or warn about.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-30 | Initial version. Extracted the workspace setup script (previously personal-only, in `.conductor/settings.local.toml`) into shared `.conductor/settings.toml` + `.conductor/scripts/{setup,restart-imposter,imposter-container}.sh`. Fixed a real bug found while extracting: `opencode-go-anthropic` provider index `2` was defined twice (`claude-opus-4-7→minimax-m3` then `claude-opus-4-8→qwen3.7-max`), silently dropping `opus-4-7` from routing since Docker's env map keeps the later `-e` for a repeated name. Resolved as `opus-4-8→qwen3.7-max`, `opus-4-7` dropped (not kept as a third index) — confirmed with repo owner. | — |
| 2026-07-30 | Fix section ordering: move `Architecture Decisions` before `Key Behaviors` per quality standards. | — |
