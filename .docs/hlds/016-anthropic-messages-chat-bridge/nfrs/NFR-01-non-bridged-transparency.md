# NFR-01: Non-bridged Anthropic traffic is unchanged

**Status:** Accepted

## Requirement

A request to any provider whose upstream API is `messages` (the default), any passthrough route, and any reserved GitHub Copilot route is forwarded exactly as before this HLD: same body bytes, same upstream path, same headers, same response bytes. Adding the option must not alter option binding, admin responses or validation for providers that do not set it.

## Verification

Run the existing Anthropic L0/L1/L2 suites unmodified, plus one byte-for-byte comparison test of a recorded Anthropic request/response through a `messages` provider before and after.

## Acceptance Criteria

- Existing Anthropic tests pass without edits.
- The byte comparison is identical for body, path, headers and streamed response.
- `GET`/`PUT` of an existing provider through the admin API round-trips with the new option absent or defaulted, without clearing it ([HLD 011](../../011-zdr-encrypted-content-sanitation/README.md) failure mode).

## Applies To

Provider options and cloning, validator, router bridge gate, forwarder header handling.
