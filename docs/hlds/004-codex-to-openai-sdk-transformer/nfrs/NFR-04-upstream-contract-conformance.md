# NFR-04: Upstream contract conformance

**Status:** Accepted

<!-- One file per quality attribute (Compatibility / Conformance). Horizontal concern.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Two binary assertions against the live strict upstream (`opencode → kimi-k2.7-code`):

1. A request produced by the normalizer — containing **only valid `function` tools** per the
   observed contract — is **accepted (HTTP 200)**.
2. The documented upstream tool-validation rules still hold: `tools[].type` ∈ {`function`, `plugin`};
   `function.name` matches `^[A-Za-z_][A-Za-z0-9_-]*$` (non-empty, no dots, no leading digit).

## Verification

The `pr_evals_gate` (L3) job (LADR-04) replays the eval matrix in
[`../examples/upstream-tool-validation.md`](../examples/upstream-tool-validation.md) against the live
upstream using the org `OPENCODE_API_KEY` and asserts the expected status codes (each unsupported
type / invalid name → 400; normalized toolset → 200).

## Acceptance Criteria

- Normalized-toolset request → 200.
- Each unsupported tool type and each invalid name → 400 with the expected error class.
- The gate is **neutral** (not failed) when `OPENCODE_API_KEY` is unavailable (e.g. fork PRs).

## Applies To

Goals 1 and 2 (the seam; request-only correctness); the `pr_evals_gate` CI job. Distinct from the
hermetic L0/L2 tests, which never touch the live upstream.
