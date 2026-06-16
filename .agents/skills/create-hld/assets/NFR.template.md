# NFR-01: <Attribute>

**Status:** Draft

<!-- One file per quality attribute (Security, Observability, Performance, Resilience,
Availability, Scalability, Compatibility, Operability, Compliance, ...). This NFR is a
horizontal concern spanning the whole HLD. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

<The measurable target. Vague NFRs ("fast", "reliable") are forbidden — pick a number or
a binary assertion. e.g. "p95 added latency ≤ 25 ms on cache hit" / "Every internal call
carries a signed service token, never the caller's raw end-user credential".>

## Verification

<How this is proven. A mechanism, not a hope. e.g. "APM span between ingress and egress" /
"Integration test asserts 403 on missing token" / "Load test before prod".>

## Acceptance Criteria

- <Observable condition that means this NFR is satisfied — the definition-of-done.>
- <Another condition.>

## Applies To

<Which goals, containers, or flows this NFR cuts across. Reference README goals or
diagram containers by name — e.g. "Goal 1; the API-gateway container".>
