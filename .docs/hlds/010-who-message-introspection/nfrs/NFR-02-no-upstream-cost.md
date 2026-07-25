# NFR-02: Zero upstream cost on match

**Status:** Accepted

## Requirement

For any request that matches the trigger (last user message is exactly `who?`,
`stream: false`, feature enabled), the proxy makes **zero** outbound HTTP calls to
any configured upstream before returning the synthetic reply. The probe round-trip
latency is in the same class as the local `/v1/models` responders — dominated by
JSON serialization, not network.

## Verification

Integration tests stub the upstream transport. The matched-probe test asserts that
the stub's request counter is zero after the probe call. A parallel latency check
records the probe's elapsed time and asserts it is less than the local
`/v1/models` endpoint's elapsed time plus a fixed margin (both in-process, both
JSON-out).

## Acceptance Criteria

- The upstream stub's request counter is zero for every matched-probe test.
- The endpoint handler returns before `forwarder.SendAsync` is reached; a spy on the
  forwarder (in L0 tests of the endpoint) asserts zero invocations on match.
- No background task (telemetry, cache warm-up, credential refresh) is triggered as a
  side effect of the probe.

## Applies To

- Goal 1 (In-band routing probe).
- The short-circuit branch in the [sequence diagram](../diagrams/c4-context.md).
