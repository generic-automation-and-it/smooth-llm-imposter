# LADR-003: Override applies to the passthrough path only; imposter routes keep config auth

**Status:** Draft

<!-- Reaffirms HLD 002 LADR-004 for this new switch. -->

## Context

The override request reads as "make Anthropic / OpenAI drop `x-api-key` and use the stored bearer." Taken
literally that could be read as dialect-wide — including **matched imposter** routes. But HLD 001's imposter
contract is that a matched route rewrites the model and applies the **provider's configured key**
([HLD 001 NFR-002](../../001-llm-imposter-routing/nfrs/NFR-002-credential-security.md)), and HLD 002 already
ruled that stored credentials touch the passthrough path only
([HLD 002 LADR-004](../../002-credential-persistence-overrides/ladrs/LADR-004-overrides-passthrough-only.md)).
The operator confirmed: the switch must affect **only** the Anthropic/OpenAI passthrough routes; anything
deviated to an imposter route uses the existing config auth.

## Decision

**Scope** the override strictly to the passthrough / default branch — the branch taken when no imposter
mapping matches and the dialect has a default provider. **Matched imposter routes are never affected by the
switch**: they keep using settings-defined keys, never read the stored credential, and never consult the
in-memory override flag. This reaffirms HLD 002 LADR-004 for the new switch rather than superseding it.

## Alternatives Considered

- **Apply the override dialect-wide (imposter + passthrough)** — rejected: makes the latency-sensitive imposter hot path's auth non-deterministic from config alone and reintroduces hidden state there, breaking HLD 001's imposter determinism.

## Consequences

- The override is evaluated on exactly one branch — the existing passthrough credential seam. The matched-imposter branch's code and timing are untouched.
- "Force Bearer on an imposter route" stays out of scope; an imposter route's auth remains a config change.
- This is the loudest non-negotiable of the design: a coder extending the switch to imposter routes is implementing the wrong thing.

## Related

- **HLD 002 LADR-004** — overrides scoped to the passthrough path (the rule this reaffirms).
- **LADR-002** — the forced-Bearer behaviour that this decision confines to passthrough.
