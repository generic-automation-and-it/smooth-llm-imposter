# NFR-02: Migration Safety

**Status:** Draft

<!-- One file per quality attribute. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

An un-migrated legacy array config (`Imposter:Providers: [ ... ]`) MUST fail fast at startup with
an actionable message, never bind silently. Because a `Dictionary<string,T>` binds a JSON array as
numeric keys (`"0"`, `"1"`, …), the validator MUST reject a provider set whose keys are purely
numeric or sequential indices, and the failure message MUST name the new dictionary shape.
Case-only duplicate keys (`opencode-go` vs `OpenCode-Go`) MUST also be rejected.

## Verification

L0 unit tests on `ImposterOptionsValidator`: (a) a dictionary with keys `"0"`,`"1"` fails
validation with a message referencing the named-provider migration; (b) keys differing only by
case fail as duplicates; (c) a normal named dictionary passes. L2 integration test: the Host
refuses to start (`ValidateOnStart`) when seeded with the legacy array shape.

## Acceptance Criteria

- A legacy `Providers: [...]` config produces a startup failure, not a running misconfiguration.
- The failure message names the `Providers: { "<name>": { ... } }` shape.
- Case-only duplicate provider keys are reported as a duplicate-name failure.
- A correctly named dictionary starts cleanly.

## Applies To

Goal 1 (hard cutover); startup validation. Realized by
[LADR-01](../ladrs/LADR-01-dictionary-keyed-providers.md) and the legacy-shape guard in
[LADR-03](../ladrs/LADR-03-resolution-mechanism.md).
