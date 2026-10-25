# NFR-01: No decryption attempt; stripped content never logged

**Status:** Accepted

## Requirement

On the ZDR sanitation path the router must not:

- read, derive, configure, or cache any key material;
- decrypt, base64-decode, parse, or otherwise interpret the value of `encrypted_content`;
- copy that value anywhere — a log event, an outbound header, a response body, an error
  message, or an exception message;
- persist it (the router is stateless by HLD 001 and has no store to persist it to).

The only operation performed on a matched item is removal from the `input` array. The only
observation made of `encrypted_content` is whether the property exists with a non-null
value — its contents are never read.

## Rationale

The decryption key is held server-side by OpenAI; that is the definition of Zero Data
Retention. A proxy that appeared to decrypt would either be wrong or be holding key
material it must not hold. Dropping is the safe operation: it weakens no ZDR guarantee,
because the ciphertext is destroyed rather than exposed.

## Verification

- Code review of the strip path: the predicate is a property lookup and a null check; the
  action is `RemoveAt`. No cipher, encoding, or key API is referenced.
- The strip emits no log statement at all, so there is no level at which content could
  leak (see `backend-logging-conventions` — content dumps are prohibited at every level,
  and this path deliberately has nothing to say).
- Grep-level check: no `Convert.FromBase64`, `Aes`, `ProtectedData`, `IDataProtector`, or
  key-material reference exists in `OpenAiRequestTransformer`.

## Acceptance Criteria

- No decryption or decoding call appears on the strip path.
- The strip path contains no logging call, so no log event can carry stripped content.
- The stripped value appears in no outbound request, response, or error body — the item is
  removed before serialization, so it cannot be re-emitted.

## Applies To

- Goal 2 (Drop the item, never decode).
- [LADR-01](../ladrs/LADR-01-drop-whole-reasoning-item.md) — enforces this by construction:
  the value is never bound to a variable.
