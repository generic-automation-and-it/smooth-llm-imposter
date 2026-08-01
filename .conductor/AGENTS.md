# AGENTS.md - Conductor Kit for SmoothLlmImposter

AI Context: shared Conductor repository scripts (`.conductor/settings.toml` + `.conductor/scripts/`) that
bring up the SmoothLlmImposter Docker container and wire `code-review-graph` on every teammate's workspace.
Human-facing walkthrough and the manual UI paste-in alternative: `.docs/wiki/setups/conductor.build-smooth-llm-imposter.md`.
Updated: 2026-08-01

## TL;DR

`.conductor/settings.toml` is committed and shared, so `git pull` is enough to get it — unlike
`.conductor/settings.local.toml`, which is machine-local and never reaches teammates. It points Conductor's
`[scripts] setup` (runs once, on workspace creation) and `[scripts.run.restart-imposter]` (on-demand trigger)
at scripts under `.conductor/scripts/`, which start the imposter container and, on setup only, wire
`code-review-graph` into four AI-coding platforms.

## Generic Kit Context

This section describes the kit's repo-agnostic behavior. It ships with the kit in every consuming repo.

### Non-Negotiables

- **The `docker run` invocation lives in exactly one place: `imposter-container.sh`.** `setup.sh` and
  `restart-imposter.sh` both call it; neither may embed its own copy of provider flags. The provider
  index bug this replaced (`opencode-go-anthropic` index `2` defined twice, silently dropping
  `claude-opus-4-7`) happened because the script existed as one long copy-pasted string with no single source
  of truth — see Changelog.
- **`run_mode` must stay `nonconcurrent`** (`.conductor/settings.toml`). The container uses a fixed name
  (`smooth-llm-imposter`) and a fixed host port (`127.0.0.1:5080` by default via `PORT`); two workspaces
  running `setup` or `restart-imposter` at the same time would race the same `docker rm -f` / `docker run`.
- **Every `code-review-graph install --platform` call in `setup.sh` keeps `--no-instructions`.** Without it,
  `install` appends an MCP-tools section to `CLAUDE.md`, which in this repository is a committed symlink to
  `AGENTS.md` — the append would land in the tracked context file.
- **Never hardcode `OPENCODE_API_KEY` / `OPENROUTER_API_KEY` here.** `imposter-container.sh` reads them from
  the workspace environment and fails fast (`:?`) if either is unset — that's Conductor's job to supply, not
  this script's.
- **Never restore `--pull=always` on the `docker run`.** It makes an unreachable or slow registry fatal
  (`exit 125`) *even when the image is already cached locally*, and by then the unconditional `docker rm -f`
  has already destroyed the working container — so a transient GHCR/DNS blip takes the router down and leaves
  nothing in its place. Refresh with a separate, failure-tolerant `docker pull` **before** the `rm -f`.
- **The `CONDUCTOR_IS_LOCAL` short-circuit belongs in `setup.sh` only**, and always as
  `${CONDUCTOR_IS_LOCAL:-0}`. `imposter-container.sh` and `restart-imposter.sh` must stay unguarded: they are
  a manual trigger, and a trigger that exits 0 without printing anything is worse than one that tries and
  fails loudly. Anything Linux-specific in the container script (the `DOCKER_HOST` default, the `dockerd`
  bootstrap) is gated on `uname -s` instead — on macOS there is no `dockerd`, and exporting `DOCKER_HOST`
  overrides the docker context Docker Desktop resolves its socket through.
- **Do NOT hoist the container start to the front of `setup.sh`.** It reads like an obvious improvement (the
  router is what every other step exists to serve, so why gate it behind best-effort tooling?) and it was
  tried and reverted. On a freshly restarted micro VM it puts `docker pull` / `rm -f` / `run` about a second
  after the daemon first answers `docker info`, instead of after the ~minute of Codex and code-review-graph
  work that had been giving a restarting daemon time to finish restoring containers and networking. Observed
  result: pull and run both succeed, then the daemon disappears during the health wait. Leave the `exec
  imposter-container.sh` at the end.
- **Failures must diagnose themselves on stdout/stderr.** This runs in a remote cloud sandbox where the
  operator has a terminal and little else — no filesystem browsing. A diagnostic that only writes a file is
  a diagnostic nobody reads. `imposter-container.sh` has a `diagnose()` that checks the **daemon first**,
  then prints container status + `docker logs --tail 100`, or (daemon down) whether a `dockerd` process
  exists, the tail of its log if this script started it, and any kernel OOM lines. Never end a failure path
  with a bare `docker logs`: when the daemon is what died, its "Cannot connect to the Docker daemon" error
  silently replaces the container output the operator came for.
- **`code-review-graph install --platform claude-code` keeps `--no-skills --no-hooks` alongside
  `--no-instructions`.** They are required because the consuming repo ships a `.claude → .agents` symlink;
  without them `install` rewrites tracked files (`CLAUDE.md`, `.claude/skills`, `.agents/settings.json`).
  Removing the symlink guards would silently re-introduce that mutation across every workspace.

### Architecture Decisions

| Decision | Rejected alternative | Why |
|---|---|---|
| Extract the container lifecycle into its own file (`imposter-container.sh`), called by both `setup.sh` and `restart-imposter.sh` | Inline the `docker run` separately in each of `[scripts] setup` and `[scripts.run.restart-imposter]` as TOML strings, matching Conductor's own simple `pnpm install`-style example | Two copies of provider flags drift silently — this is exactly how the `opencode-go-anthropic` index-2 collision happened in the personal script this replaced. |
| Commit `.conductor/settings.toml` + `.conductor/scripts/*.sh` | Keep the workspace script only in `.conductor/settings.local.toml` (as it existed before this change) | `settings.local.toml` is machine-local; every teammate had to hand-paste the script into their own workspace to get it at all. |
| Keep `code-review-graph install --platform claude-code` (flag-gated) rather than excluding the platform entirely | Skip `claude-code` outright, as the original wiki doc did, because default `install` mutates tracked files | `--no-instructions --no-skills --no-hooks` gets the same safety (only writes untracked `.mcp.json`) without losing the fourth platform's code-intelligence coverage. |
| Separate `docker pull` (tolerating failure) before `docker rm -f`, then plain `docker run` | `docker run --pull=always`, which reads as the obvious way to keep the tag fresh | `--pull=always` makes registry reachability a hard dependency of *starting the service at all*: verified `exit 125` with the image present locally and the registry unresolvable. Combined with the unconditional `rm -f` that precedes it, a network blip is not "runs a stale image" but "no container". |
| A `default = true` run script plus `auto_run_after_setup = true` | Leave `restart-imposter` as a named, non-default trigger and rely on `setup` | `setup` fires only on workspace *creation*; Conductor's schema has no resume hook. Without a default run script there is no lifecycle entry point that re-establishes the container, so a restart leaves the workspace with no router and no automatic way back. |
| `imposter-logs` as its own run script (`docker logs -f`) | Fold log-following into `restart-imposter`, or tell operators to run `docker logs` themselves | `-f` blocks forever, so it cannot live in a path that must finish; and "run it yourself" assumes shell familiarity plus knowing the container name and which of `docker` / `sudo docker` works in this sandbox. A button that already checks the daemon and the container's existence answers the question in one press. |
| Start `dockerd` with `sudo -n setsid nohup` | `sudo nohup dockerd &`, which looks sufficient | `nohup` only covers SIGHUP. A Conductor run script's process group is torn down when the command exits, and a terminal the operator closes takes its group with it — either would reap a daemon that is only `nohup`-protected. `setsid` moves it into its own session. |
| Keep the ~28 `-e Imposter__Providers__*` flags in `imposter-container.sh` and ship them with the kit | Replace them with an `Imposter__*` environment pass-through so mappings live in one file per machine | Tried and reverted. A kit that routes nothing until the consumer also authors and places a mapping file is a worse product than one that works on install. Compounding it: provider keys contain hyphens, so bash cannot set them at all, and the pass-through implementation silently dropped every route. Duplicate-mapping drift across repos is the accepted cost. |

### Key Behaviors

- **Session forwarding opt-out** — The image default is `SessionForwarding: opencode-go` on both
  `opencode-go-*` providers, so matched routes stamp `session_id` / `x-opencode-session`. To stop OpenCode
  session token usage, uncomment the two `OPENCODE_GO_{ANTHROPIC,OPENAI}_SESSION_FORWARDING` exports,
  add both names to `--preserve-env`, and add the two `-e` flags to the `docker run`.
- **Enabling this in a new workspace.** Nothing to configure beyond secrets: once
  `.conductor/settings.toml` is on the branch a workspace is created from, Conductor runs its `setup` script
  automatically. The only prerequisite is that the workspace has `OPENCODE_API_KEY` and `OPENROUTER_API_KEY`
  set as environment variables (Conductor workspace/environment settings, not committed anywhere) — without
  them `imposter-container.sh` exits immediately with a `:?` message naming the missing variable.
- **Running the trigger.** `restart-imposter` is the workspace's default run script (icon `refresh-cw`) —
  run it any time to recreate the container without recreating the workspace: after a VM restart, pulling a
  new image tag, rotating either API key, or recovering from a crash-looped container. It skips the Codex and
  `code-review-graph` steps entirely (only `setup.sh` runs those), so it is also the honest way to test the
  container path in isolation.
- **Precedence gotcha.** Conductor resolves settings per-value across layers — "if two layers set the same
  value, Conductor uses the highest layer that applies" (repository local outranks repository shared). The
  docs don't specify the merge granularity below that (e.g. whether a local `[scripts] setup` masks only that
  key or the whole `[scripts]` table). Practically: a teammate who already has a personal
  `settings.local.toml` with its own `setup` value will keep running that instead of this file's `setup.sh`,
  silently, until they delete or reconcile it — verify which one actually ran (check for the
  code-review-graph install log lines, only present via this file's `setup.sh`) rather than assuming.
- **Cloud-sandbox assumptions, still not verified on local macOS.** The Linux-only branch in
  `imposter-container.sh` (starting `dockerd` with `sudo -n nohup`, defaulting `DOCKER_HOST`) matches the
  Amazon Linux 2023 cloud sandbox lifecycle, which has no systemd as PID 1. The macOS path — skip that
  branch, let the docker CLI resolve Docker Desktop's socket through the active context — is written but has
  only been reasoned about, not run: nobody has executed `restart-imposter` on a Mac yet. `[scripts] setup`
  has no `available_in` gate — unlike `[scripts.run.*]` — so it runs unconditionally on every workspace,
  local or cloud; it is the `CONDUCTOR_IS_LOCAL` guard, not Conductor, that makes it a no-op there. Local
  users who hit failures can still override `setup` via a personal `settings.local.toml` (see the precedence
  gotcha above).
- **Idempotent recreate, not incremental update.** Every run (`setup` or `restart-imposter`) does
  `docker rm -f` then `docker run -d` unconditionally — there's no update-in-place path, no volumes to lose,
  and no state carried between recreations beyond what's baked into the image + the `-e` flags. The one
  exception is a pull that fails with no locally cached image: the script exits 1 *before* `rm -f`, leaving
  whatever is already running alone rather than trading a working container for none.
- **Lifecycle coverage across a restart.** `[scripts] setup` runs when a workspace is *created*; the schema
  has no resume/reboot hook. On a cloud micro VM, `dockerd` is started by `imposter-container.sh` itself
  (no systemd as PID 1), so nothing survives a VM restart on its own — `--restart unless-stopped` only helps
  once a daemon is back. The run script is therefore the mechanism that re-establishes the container, which
  is why it is `default = true` and `auto_run_after_setup = true`.
  - The `default = true` flag on `restart-imposter` makes the restart button prominent in the Conductor UI so
    a user can re-establish the container after a cloud micro-VM restart. The companion
    `auto_run_after_setup = true` only fires on new **local** workspaces (per the Conductor schema), so on
    cloud it is a no-op — recovery on cloud is user-initiated via the prominent run button.
- **Mappings stay hardcoded in `imposter-container.sh`, and the kit ships them as-is.** This was tried the
  other way — the `-e Imposter__Providers__*` flags were replaced by an `Imposter__*` environment
  pass-through — and reverted. Reasons, in order of how much they cost to learn:
  1. **A consuming repo should get a working router from the install, not a configuration exercise.** The
     kit's value is that it runs; a generalised kit that routes nothing until you also author and place a
     mapping file is a worse product.
  2. **Provider keys contain hyphens (`opencode-go-anthropic`), and bash cannot hold them.** `export
     'Imposter__Providers__opencode-go-anthropic__Dialect=x'` fails with *"not a valid identifier"*, so
     nothing shell-based can set these. Only `docker run -e NAME=value` (as written here) or `--env-file`
     can express them at all.
  3. Two bugs followed from ignoring (2): a `grep -oE '^Imposter__[A-Za-z0-9_]+'` that truncated every key
     at its first hyphen — forwarding the non-existent `Imposter__Providers__opencode` so **every route
     silently vanished** — and a `done < <(…)` process substitution that cannot work here at all, because
     **this sandbox has no `/dev/fd`** (`/dev/fd/63: No such file or directory`, quietly, leaving the loop
     empty).

  A repo that wants different routes edits its own copy of the script. `PORT` and `SMOOTH_LLM_IMAGE`
  remain environment-overridable for the cases that actually vary per workspace.
- **Known defect: `setup.sh` writes `.git/info/exclude` by literal path.** A local Conductor workspace is a
  git *worktree*, where `.git` is a file rather than a directory, so that path does not resolve, the
  `grep … || echo …` list fails, and `set -e` aborts setup before the container ever starts. `git rev-parse
  --git-path info/exclude` is the portable resolution (plus `mkdir -p` on its parent, which is not
  guaranteed to exist). A fix was written and then reverted along with the rest of the generalisation work;
  it is unrelated to packaging and should land on its own.
- **`--no-skills --no-hooks` on `claude-code` assumes this repo's layout.** A consuming repo without the
  `.claude → .agents` symlink gets those features suppressed for no reason — harmless, but not what it
  would choose. Gate on the symlink if the kit ever needs to be polite about it. (The invariant that those
  flags MUST stay is enforced under Non-Negotiables.)
- **The MCP servers `code-review-graph install` configures all invoke `uvx code-review-graph serve`**,
  regardless of platform. If a workspace's snapshot doesn't have `uv` on `PATH`, the generated MCP configs are
  written successfully but the servers themselves cannot start — `code-review-graph build` still exits 0 in
  that case, so the graph looks built but no agent can reach it over MCP.
- **Project-scoped MCP configs are hidden via `.git/info/exclude`, not `.gitignore`.** `setup.sh` seeds
  `.mcp.json` (from `claude-code`) and `opencode.jsonc` (from `opencode`) into `.git/info/exclude` so they
  never show up in a workspace diff, without editing the tracked `.gitignore`. `code-review-graph install`
  still appends `.code-review-graph/` to the tracked `.gitignore` directly (all platforms, predates this
  script) — that one line is expected to show up as a diff in every workspace.
- **Conductor settings-layer asymmetry (verified from the published schemas).** The user schema
  (`settings.schema.json`) has `environment_variable_files` but **not** `environment_variables`; the repo
  schema (`settings.repo.schema.json`) has both. Whether a Mac's `~/.conductor/settings.toml` reaches a
  cloud sandbox at all is **unverified** — nobody has run that test. Recorded because it was the premise
  of the mapping-externalisation attempt above; since mappings now ship in the script, the kit does not
  depend on the answer either way.
  Whether `environment_variable_files` expands `~` is likewise unverified — use absolute paths if you ever
  rely on it.

### Migration Plans

Any teammate's pre-existing `.conductor/settings.local.toml` that duplicates this shared script's container
logic should be deleted once its behavior is confirmed equivalent — per the precedence gotcha above, a
lingering local file silently masks every update made here. There's no forcing function for this; it's a
manual cleanup step per teammate, not something this script can detect or warn about.

## Repo-Specific Context

This section contains SmoothLlmImposter-specific details that do not ship with the kit.

### Current imposter model mappings

| Dialect | Incoming model | Upstream provider | Upstream model | Upstream API |
|---|---|---|---|---|
| Anthropic | `claude-sonnet-4-6` | OpenCode Go | `qwen3.6-plus` | N/A |
| Anthropic | `claude-opus-4-6` | OpenCode Go | `qwen3.7-plus` | N/A |
| Anthropic | `claude-opus-4-8` | OpenCode Go | `qwen3.7-max` | N/A |
| Anthropic | `claude-haiku-*` | OpenRouter | `inclusionai/ling-3.0-flash:free` | N/A |
| OpenAI | `gpt-5.4` | OpenCode Go | `kimi-k2.7-code` | `chat_completions` |
| OpenAI | `gpt-5.5` | OpenCode Go | `glm-5.2` | `chat_completions` |
| OpenAI | `gpt-5.6-luna` | OpenCode Go | `grok-4.5` | `responses` |

_For the live route mappings, see `.conductor/scripts/imposter-container.sh` — this table is regenerated
from the script's `-e Imposter__Providers__*` exports on each kit release and is not the source of truth._

These are setup-specific mappings chosen for this Conductor environment. They intentionally differ from the
illustrative mappings and caching choices in
[HLD 001](../../hlds/001-llm-imposter-routing/README.md#configuration); the HLD is not the runtime source of
truth for this script. OpenCode Go target IDs are bare upstream strings with no `opencode-go/` prefix,
consistent with the live-upstream
[`OpencodeToolNormalizationEvalTests.cs`](../../../tests/SmoothLlmImposter.Upstream.EvalTest/OpencodeToolNormalizationEvalTests.cs).
OpenRouter targets keep the provider-prefixed slug the OpenRouter API expects (here `inclusionai/ling-3.0-flash:free`).

Inbound API model names (the `From` column above) are imposter-side aliases — they are what
clients send to the proxy. The `To` column names the upstream wire ID, which is the identifier
the upstream provider uses on its own API. For the OpenAI row `gpt-5.6-luna → grok-4.5`, the imposter
accepts the OpenAI-style alias and forwards to xAI; see
[OpenAI's model index](https://platform.openai.com/docs/models) for the imposter-side namespace
and [xAI's model index](https://docs.x.ai/docs/models) for the upstream wire ID.

The image default enables session identity forwarding (`SessionForwarding: opencode-go`) for the OpenCode Go
providers, so matched routes stamp `session_id` and `x-opencode-session`. The shared workspace script uses this
default as-is. To stop OpenCode session token usage, uncomment the two exports, add both names to
`--preserve-env`, and add the two `-e` flags to the `docker run`.

### Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-30 | Initial version. Extracted the workspace setup script (previously personal-only, in `.conductor/settings.local.toml`) into shared `.conductor/settings.toml` + `.conductor/scripts/{setup,restart-imposter,imposter-container}.sh`. Fixed a real bug found while extracting: `opencode-go-anthropic` provider index `2` was defined twice (`claude-opus-4-7→minimax-m3` then `claude-opus-4-8→qwen3.7-max`), silently dropping `opus-4-7` from routing since Docker's env map keeps the later `-e` for a repeated name. Resolved as `opus-4-8→qwen3.7-max`, `opus-4-7` dropped (not kept as a third index) — confirmed with repo owner. | — |
| 2026-07-30 | Fix section ordering: move `Architecture Decisions` before `Key Behaviors` per quality standards. | — |
| 2026-07-30 | Swapped the OpenRouter Anthropic (`openrouter-anthropic`) haiku route target from `tencent/hy3` to `inclusionai/ling-3.0-flash:free` in `imposter-container.sh` (`Imposter__Providers__openrouter-anthropic__Models__0__To`) and documented the current model mappings in Key Behaviors. | — |
| 2026-08-01 | Restart-survivability fixes, from a report that the imposter did not come back after a micro-VM restart. (1) Dropped `--pull=always` for a failure-tolerant `docker pull` before `docker rm -f` — verified `exit 125` with the image cached locally and the registry unresolvable, i.e. the old flag turned a DNS blip into a destroyed container. Verified after: unreachable registry + cached image → container recreated and healthy; unreachable registry + no cached image → exit 1 with the running container untouched. (2) Removed the `CONDUCTOR_IS_LOCAL` guard from `imposter-container.sh` (kept in `setup.sh`, now `${CONDUCTOR_IS_LOCAL:-0}` — a bare reference under `set -u` aborted any hand-run with `unbound variable`), so the manual trigger is no longer a silent no-op; Linux-only daemon bootstrap and `DOCKER_HOST` default now gated on `uname -s`; `dockerd` started under `setsid`. (3) `settings.toml` gained `default = true` + `auto_run_after_setup = true`, and a second `imposter-logs` trigger. | — |
| 2026-08-01 | **Reverted within the same day: hoisting the container start to the front of `setup.sh`.** Field result on a restarted micro VM: `docker pull` and `docker run` both succeeded, then the daemon went unreachable during the 30s health wait, and the failure path's bare `docker logs` reported "Cannot connect to the Docker daemon" — swallowing the container logs. Two lessons kept in the code: the ordering is now explicitly pinned (see Non-Negotiables), and failures print a daemon-first `diagnose()` bundle to the terminal instead of writing files an operator on a remote sandbox cannot read. Root cause of the daemon death is still unconfirmed — `/tmp/dockerd.log` was empty, which means that daemon was not started by this script. | — |
| 2026-08-01 | Packaged the kit for reuse: `.conductor/install.sh` (vendoring installer, `--ref`, `--check`) and `.github/workflows/publish-conductor-kit.yml` (tarball + SHA256SUMS on a GitHub Release). `.conductor/AGENTS.md` split into generic kit context and repo-specific context. **The scripts ship as-is** — an attempt to generalise them (replacing the `-e Imposter__Providers__*` flags with an `Imposter__*` environment pass-through, plus a worktree fix and conditional claude-code flags) was reverted in full: the pass-through silently dropped every route, and a kit that routes nothing until the consumer configures it is a worse product. See the Architecture Decisions row and the two Key Behaviors entries recording what was learned and what defect remains open. | — |
| 2026-08-01 | Fix `publish-conductor-kit.yml`: split release creation and publication into two steps. The action cannot upload assets to a published release on an immutable-release repository, and the previous single-step create+upload burned v0.0.1 and v1.0.0 (permanently asset-less, irrepairable). Create as draft, upload assets into the draft, then `gh release edit --draft=false` with `make_latest` placed on the publish step (drafts cannot be latest). | #108 |