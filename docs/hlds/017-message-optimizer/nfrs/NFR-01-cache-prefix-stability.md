# NFR-01: Cache-prefix stability

**Status:** Draft

## Requirement

While a session's cache scope is retained (LADR-05), every human message the stage has cached is forwarded with byte-identical text on every later request, whatever happened to it — rewritten, declined by the gate, discarded by validation, skipped by an open circuit, or left unprocessed when the per-request budget ran out. A message may change only on its first handling: a new message, or history first seen after enablement, a restart or a TTL expiry (one accepted cache miss, Open item 3). Requests without a captured session id are not cached and are outside this requirement.

## Verification

An L2 replay of a multi-turn Claude Code–shaped conversation — tool-call continuations, a gate failure mid-way, an open optimizer circuit and a trial call — compares each forwarded body's messages with the previous forward of the same conversation, byte for byte. Repeated on the NativeAOT executable for `linux/amd64` and `linux/arm64`.

Implemented as `MessageOptimizerCachePrefixStabilityTests` (L2). One replay resends the whole history every turn through a gate decline, an unusable optimizer answer (a failure that does not open its circuit), a rewrite discarded by validation, a gate failure, an optimizer failure, both circuits open, and a trial call that closes them, and compares the raw bytes of every earlier message with the previous forward. Separate cases cover the sliding TTL (kept at 40+40 minutes with a one-hour TTL, dropped after 61 idle minutes) and requests with no session or only a derived one (never cached). Each rule was checked by mutating the production code and watching the intended case fail. The native replay was run on the host-platform NativeAOT executable only; `linux/amd64` and `linux/arm64` remain native-runner gates.

## Acceptance Criteria

- Every cached message in a retained scope is forwarded with byte-identical text on every later request.
- A scope used within its sliding TTL is never dropped; one idle past it is.
- A circuit closing does not cause an earlier, already-handled message to be rewritten.

## Applies To

Goal 4; LADR-03, LADR-05, LADR-07.
