# NFR-04: Security (secret confidentiality)

**Status:** Accepted

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

The synthesized response contains only model identifiers (`to` values) and provider names
(`owned_by`). It **never** contains a provider `Secret`, a stored credential, a base URL, or any
auth-header material. This upholds the routing feature's standing non-negotiable: provider secrets
live only in configuration and are never logged, echoed, or serialized into a response.

## Verification

- L0/L2 test asserts the response body contains none of the configured `Secret` values for any
  provider in the test catalogue.
- Code review confirms the discovery-JSON builder reads only `to` and provider `Name`, never the
  `Secret`, `BaseUrl`, or `AuthScheme` fields, into the response.

## Acceptance Criteria

- No provider `Secret` substring appears in the response body under any tested configuration.
- The builder's inputs are limited to model `to` values and provider names.

## Applies To

The whole HLD; the Application discovery-JSON builder
([LADR-04](../ladrs/LADR-04-synthetic-model-fields.md)).
