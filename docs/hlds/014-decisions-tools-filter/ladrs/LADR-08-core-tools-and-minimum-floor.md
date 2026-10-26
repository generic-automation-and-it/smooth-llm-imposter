# LADR-08: Always-kept core tools and a minimum-kept floor

**Status:** Accepted

## Context

- `kev-4b` scores are compressed for vague requests: a long Claude Code session scored all 57 asked tools between 0.1 and 0.58 for "analyse the issue now…" while the 7 history-protected tools were never asked, so a 0.60 threshold sent 7 of 64 tools.
- Core tools (shell, read, edit) scored 0.07–0.65 even with correct state; no single threshold both prunes concrete requests and keeps them for vague ones (kept counts at a 0.30–0.50 sweep ranged 0–37 of 64).
- Dropping a needed tool breaks the task; keeping an unneeded one costs prompt tokens. The error costs are asymmetric.
- Counting only asked tools in an all-skip guard made vague requests forward 64/64 tools (3 of 4 measured requests), i.e. no filtering.

## Decision

**Never ask about, and never remove**, tools named in `AlwaysKeep` (default `Bash,Read,Edit,Write,Skill`; comma-separated; exact, case-sensitive match against tool name, MCP server label or hosted type). They skip the classifier, so they also save latency.

**Capability gateways** `ToolSearch` and `Agent` are always kept in addition to `AlwaysKeep` and cannot be configured away. Each is the only route to a whole tool class (every deferred tool, every subagent), and a single-request classifier cannot judge that need: a 12-tool Claude Code catalog lost both for a request that needed neither (2026-10-26 probe).

**Floor**: after thresholding, restore the highest-scoring removed tools (ties by catalog order) until at least `MinKeepTools` (default 20) tools remain, counting always-kept, history-protected and unknown tools. A catalog with `MinKeepTools` or fewer tools makes no classifier call. `0` disables the floor.

**Guard**: a pass that would leave no tool still keeps the catalog.

**Threshold** returns to 0.50: it now only decides which tools are confidently needed; the floor and core list absorb a miscalibrated classifier.

## Consequences

- Vague requests keep about `MinKeepTools` tools, chosen by rank, instead of 7 or all.
- A high-scoring tool is never dropped for being below the floor; a needed tool ranked below the floor can still be dropped.
- Default core names are Claude Code's; other clients need `AlwaysKeep` set to their own tool names.
- A deployment that overrides `AlwaysKeep` still keeps the capability gateways; a client tool that happens to be named `ToolSearch` or `Agent` is never pruned.
- Ranking quality still depends on the state sent to the classifier.
