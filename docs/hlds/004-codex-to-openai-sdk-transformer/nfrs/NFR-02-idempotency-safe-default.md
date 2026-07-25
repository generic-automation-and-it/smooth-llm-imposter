# NFR-02: Correctness — idempotency and safe default

**Status:** Accepted

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Two binary guarantees:

1. **Safe default** — with normalization unset/off for a provider, the forwarded request body is
   **byte-identical** to current (pre-feature) behavior.
2. **Idempotent** — applying normalization to an already-normalized request body produces the same
   body (`normalize(normalize(x)) == normalize(x)`).

## Verification

- L0 unit test: with the opt-in off, the transformer output equals the input that today's pipeline
  would forward (byte-for-byte for the body it controls).
- L0 unit test: feeding a normalized body back through normalization returns it unchanged.

## Acceptance Criteria

- A provider that has not opted in shows no body diff versus the current build for the same request.
- Re-running normalization is a no-op on its own output.

## Applies To

Goal 3 (safe default) and Goal 4 (composability); the normalization seam. Underwrites LADR-03.
