# NFR-01: Cache-prefix stability

**Status:** Draft

## Requirement

While a session's cache scope is retained (LADR-05), every human message the stage has cached is forwarded with byte-identical text on every later request, whatever happened to it — rewritten, declined by the gate, discarded by validation, or skipped by an open circuit. A message may change only on its first handling: a new message, or history first seen after enablement, a restart or a TTL expiry (one accepted cache miss, Open item 3). Requests without a captured session id are not cached and are outside this requirement.

## Verification

An L2 replay of a multi-turn Claude Code–shaped conversation — tool-call continuations, a gate failure mid-way, an open optimizer circuit and a trial call — compares each forwarded body's messages with the previous forward of the same conversation, byte for byte. Repeated on the NativeAOT executable for `linux/amd64` and `linux/arm64`.

## Acceptance Criteria

- Every cached message in a retained scope is forwarded with byte-identical text on every later request.
- A scope used within its sliding TTL is never dropped; one idle past it is.
- A circuit closing does not cause an earlier, already-handled message to be rewritten.

## Applies To

Goal 4; LADR-03, LADR-05, LADR-07.
