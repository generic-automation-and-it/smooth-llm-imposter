# NFR-02: Secret Confidentiality (Security)

**Status:** Accepted

## Requirement

No upstream secret value ever appears in a routing-config API response or in any log line. The
provider-config boundary neither returns nor accepts a secret; the credential boundary accepts a secret on
write but never returns it on read.

## Verification

- Integration test (L2): a provider-config `GET`/list response body is asserted to contain no secret field
  and no secret value; a credential `GET`/list response is asserted to omit the secret.
- Log assertion test: drive create/update/rotate through the admin API and assert the captured log output
  contains none of the supplied secret values (reuse the HLD 007 NFR-03 / credential no-leak log-capture
  pattern).

## Acceptance Criteria

- Provider-config `GET` is round-trippable into `PUT` **without** a secret field present.
- No admin endpoint returns a plaintext secret or, for the database backend, ciphertext.
- No `Debug`/`Information`/`Warning`/`Error` log line emitted by the config or credential paths contains a
  secret value.

## Applies To

Goal 4; the routing-config and credential boundaries
([LADR-02](../ladrs/LADR-02-config-secret-boundaries.md),
[LADR-05](../ladrs/LADR-05-settings-backed-provider-keyed-credentials.md)). Upholds HLD 002 / HLD 007
secret-confidentiality posture.
