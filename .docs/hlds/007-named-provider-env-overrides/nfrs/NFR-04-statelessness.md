# NFR-04: Statelessness

**Status:** Prototype

<!-- One file per quality attribute. Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Provider configuration and conventional override resolution MUST remain pure config/env reads. The
resolver MUST NOT introduce persistence, network I/O, or any external dependency: it reads
`IConfiguration` and environment variables at startup and writes onto in-memory options only. Zero
database connections and zero outbound requests are added by this design. The router stays
stateless and key-less.

## Verification

L2 integration test: boot the Host with the named-dictionary config and conventional env vars set;
assert startup opens no database connection and issues no outbound HTTP. Code review: the
post-configure type depends only on `IConfiguration` / options, not on any client, store, or
`HttpClient`.

## Acceptance Criteria

- Resolving and validating provider config opens zero DB connections and makes zero upstream calls.
- The resolver's only inputs are `IConfiguration` and process environment variables.
- No new persisted state is introduced for providers or secrets.

## Applies To

The whole HLD; the configuration/startup path. Reinforces the router's stateless, key-less posture
described in the root design. Realized by
[LADR-03](../ladrs/LADR-03-resolution-mechanism.md).
