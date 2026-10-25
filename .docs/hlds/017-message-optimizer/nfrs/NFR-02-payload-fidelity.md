# NFR-02: Payload fidelity

**Status:** Draft

## Requirement

Zero forwarded rewrites alter, drop or add a protected element — identifier, path, URL, number, version, code span, environment variable, negation, markdown structure — measured over a fixture corpus covering each element class and redacted real Claude Code and Codex messages.

## Verification

L0 fixtures drive the deterministic check with a rewrite that breaks each protected class and assert it is rejected; a passing rewrite per class asserts it is accepted. A corpus run against a live optimizer model counts accepted rewrites that a second, independent check flags. Repeated on the NativeAOT executable.

## Acceptance Criteria

- Every protected class has a rejecting and an accepting fixture.
- The corpus run reports zero accepted rewrites with a changed protected element, and the rejected-rewrite rate.
- An optimizer answer that echoes harness or instruction text is rejected.

## Applies To

Goal 3; LADR-03, LADR-04.
