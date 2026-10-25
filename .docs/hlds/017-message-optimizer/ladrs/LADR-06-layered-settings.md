# LADR-06: Layered decision settings and switch resolution

**Status:** Draft

## Context

- Operators run one decision model for both the tool filter and the message gate, and want to set it once.
- Each section may still need its own model or endpoint.
- One shared enable flag would let a single variable turn on two features.

## Decision

**Resolve each decision setting per section: section value → `DECISIONS_*` → built-in default.** Shared `DECISIONS_*` covers `BASE_URL`, `PATH`, `MODEL`, `MIN_PROBABILITY`, `MAX_ATTEMPTS`, `TIMEOUT` and `CIRCUIT_BREAK_SECONDS`, and also `MIN_KEEP`, `ALWAYS_KEEP` and `STICKY_PER_SESSION`, which default the tool section only. There is no `DECISIONS_ENABLED`. Existing `DECISIONS_TOOLS_*` configurations keep their effective values. Structured configuration mirrors the same layering.

`LLM_MESSAGES_*` is a complete, separate set — `ENABLED`, `BASE_URL`, `PATH`, `MODEL`, `MAX_ATTEMPTS`, `TIMEOUT`, `CIRCUIT_BREAK_SECONDS`, `MIN_CHARS`, `API_KEY` — with no fallback (LADR-01). `DECISIONS_MESSAGES_ENABLED` controls only the gate; when unset it takes the value of `LLM_MESSAGES_ENABLED`:

| `LLM_MESSAGES_ENABLED` | `DECISIONS_MESSAGES_ENABLED` | Result |
|---|---|---|
| `false` / unset | any | Feature off; body forwarded unchanged |
| `true` | unset or `true` | Gate, then optimizer on a positive answer |
| `true` | `false` | Gate bypassed; every candidate goes to the optimizer |

Only settings for parts that are effectively on are validated. `DECISIONS_MESSAGES_ENABLED=true` with the optimizer off has no effect and logs one startup line.

## Consequences

- One variable configures the shared model; an override changes one section.
- Validation depends on the effective switch state, which must be tested per combination.
- Every new variable must reach containers through the setup scripts' forwarding lists, or it is silently absent.
