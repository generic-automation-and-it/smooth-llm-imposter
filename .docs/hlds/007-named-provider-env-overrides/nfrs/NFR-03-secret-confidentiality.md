# NFR-03: Secret Confidentiality

**Status:** Accepted

<!-- One file per quality attribute. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

The conventional resolver MUST NOT emit secret **values** to any sink. A resolved `_API_KEY` /
`Secret` value MUST never appear in logs, validation failure messages, or exceptions. Conventional
variable **names** (e.g. `OPENCODE_GO_API_KEY`) and the fact that a value was applied MAY be logged
at `Debug`, but the value MUST NOT. This preserves the router's key-less posture: secrets enter
only from config/env and are never persisted (consistent with the stateless design).

## Verification

L0 unit test: drive the post-configure resolver with a populated `_API_KEY` env var and a capturing
logger; assert no log entry or thrown message contains the secret value. Code review: the resolver
and validator never interpolate `Secret` into a string. Reuse of the existing secret-handling
conventions from HLD 002 / NFR-04 of HLD 005.

## Acceptance Criteria

- No log record at any level contains a resolved secret value.
- Validation/exception messages reference provider keys and field names only, never secret values.
- Applying a conventional secret logs at most the variable name + provider at `Debug`.

## Applies To

Goal 2 (conventional env surface); the post-configure resolver and the validator. Realized by
[LADR-02](../ladrs/LADR-02-conventional-env-surface.md) /
[LADR-03](../ladrs/LADR-03-resolution-mechanism.md).
