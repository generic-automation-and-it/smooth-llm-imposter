# imposter-newsession — AGENTS.md

## TL;DR

Non-streaming `--newsession` probe runner. Mints a synthetic session id on the SmoothLlmImposter router and stores a caller-to-synthetic mapping in the router's in-memory translation dictionary. Works around the streaming-harness limitation (LADR-05) that silently bypasses the in-band switch.

## Key Behaviors

- **Why a script, not an in-band trigger.** Same `stream:true` reason as `imposter-who`: the router refuses to synthesize `--newsession` replies on streaming requests (LADR-05), and agent harnesses stream by default. The skill's script issues a non-streaming curl so the short-circuit fires and the mapping is minted.
- **Session identity required.** `--newsession` is a no-match when the caller has no resolvable session id (by design — it is never an error, just a no-op). The router's `SessionIdentityResolver` reads the `session_id` header first, so the script sends the caller id as that header for both dialects. Without a resolvable id the router forwards normally and the real upstream answers `--newsession` as an ordinary turn.
- **Session forwarding opt-in.** The router only resolves a session id when the matched provider has `SessionForwarding` opted in (HLD 009). When the router was started with `OPENCODE_GO_*_SESSION_FORWARDING=none` (the Conductor default), `--newsession` is a no-match regardless of `--session`. The mint only takes effect against an opt-in provider.
- **Idempotent mint.** Re-running with the same caller id reuses the existing synthetic id (the router logs "reused existing", not "newly inserted"). The mapping is process-lifetime only (HLD 010, LADR-06) and does not survive a router restart.

## Coupling Hazards

- **Session header name.** Same coupling as `imposter-who`: the script sends `session_id` as a header because the resolver reads headers first. If `SessionIdentityResolver.HeaderCandidates` ever changes, update both skills.
- **LADR-05 reversal.** See `imposter-who` coupling notes.
- **SessionForwarding default.** If the operator's default ever flips to opt-in, the `--session` requirement still stands, but the documented gotcha about `OPENCODE_GO_*_SESSION_FORWARDING=none` should be removed.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-25 | Initial version. | |
