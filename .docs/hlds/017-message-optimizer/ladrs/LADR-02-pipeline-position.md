# LADR-02: At the forward seam, after switches and routing transforms

**Status:** Draft

## Context

- Imposter switches (`--who?`, `--newsession`, HLD 010) are user text with exact-match semantics; a rewrite could change or remove them.
- The tool filter must see transformed tools — normalization reshapes them (HLD 004) — so it runs at the post-transform forward seam (HLD 014 LADR-05).
- Message and tool optimization run in parallel (LADR-08), so both must start from the same body at the same seam.

## Decision

**Run the message optimizer at the HLD 014 forward seam**, after the who-probe short circuit, session translation and routing transforms, on the same post-transform body the tool filter receives. A who-probe never reaches the seam, so it costs no model call. The optimizer replaces only human text values; every other field of a message — including `cache_control` markers added by caching injection — is kept.

The tool filter classifies the original text, because the two branches start together; session identity still comes from the inbound request (HLD 014 LADR-09).

## Consequences

- One seam, one body, two independent branches.
- The optimizer sees post-transform shapes: Messages and Chat Completions, including a Chat body produced by the Responses-to-Chat downgrade (Responses scope is Open item 4).
- Time-to-first-byte is the slower branch, not the sum.

## Related

- **HLD 014 LADR-05** — the seam, unchanged.
- **LADR-08** — how the branches run and merge.
