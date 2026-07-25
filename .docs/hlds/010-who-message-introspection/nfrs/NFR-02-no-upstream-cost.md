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
the stub's request counter is zero after the probe call. No timing assertion is made
— the probe's latency class is a side effect of being in-process JSON serialization,
not a contract; asserting a fixed margin against another endpoint would be flaky on
loaded CI workers without adding any coverage the counter check does not already give.

## Acceptance Criteria

- The stub upstream request counter is zero for `who?` requests (probe path
  never invokes the forwarder).
- No background task is scheduled by the probe path.

## Applies To

- Goal 1 (In-band routing probe).
- The short-circuit branch in the [sequence diagram](../diagrams/c4-context.md).
