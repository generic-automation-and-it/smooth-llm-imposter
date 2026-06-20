# NFR-01: Determinism

**Status:** Draft

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

For a fixed catalogue, `GET /openai/v1/models` returns a **byte-identical** response body on every
call and on every instance. No field is derived from wall-clock time, randomness, request state, or
upstream availability. Specifically, `created` is a configuration-independent constant and the
`data` array order is a deterministic function of catalogue order.

## Verification

- L0 unit test calls the aggregation twice with the same options and asserts the two serialized
  bodies are equal.
- L0 unit test asserts every entry's `created` equals the fixed constant (not "approximately now").
- L2 integration test asserts the `data` ordering is stable across repeated calls.

## Acceptance Criteria

- Two consecutive responses under identical configuration are equal byte-for-byte.
- No `created`/`id`/order value changes between calls or between process restarts.
- The test suite contains an explicit determinism assertion (not incidental).

## Applies To

Goal 2 (schema-valid, no-dependency response); the Application discovery-JSON builder
([LADR-04](../ladrs/LADR-04-synthetic-model-fields.md)).
