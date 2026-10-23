# NFR-02: First-consumer compatibility

**Status:** Draft

## Requirement

Extraction produces no observable regression in HLD 014's operator settings or tool-selection contract. Existing flat/structured defaults and precedence resolve identically. Disabled and no-drop bodies retain their original instance; successful pruning, ordering, history/choice protection and partial fail-open outcomes remain equivalent. No default configuration activates a consumer.

## Verification

Compare pre-extraction and shared-integration behavior using HLD 014's configuration, API golden, failure, cancellation and downgrade fixtures. Verify startup validation when all consumers are disabled and when tool filtering is enabled. Exercise affected configuration and decision calls on published native artifacts; preserve Linux AMD64/ARM64 native-runner gates.

## Acceptance Criteria

- All seven existing settings keep their documented effective values, including conventional precedence.
- Existing tool-filter output and no-call/byte-identity assertions pass unchanged.
- Classifier extraction does not change inference routing or introduce endpoints.
- Native startup and decision-call behavior match the JIT contract; absent platform evidence is reported, not inferred.

## Applies To

Configuration resolution and HLD 014 integration; Goal 3.
