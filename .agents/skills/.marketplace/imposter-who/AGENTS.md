# imposter-who — AGENTS.md

## TL;DR

Non-streaming `--who?` probe runner. Sends a single curl to the SmoothLlmImposter router and prints `Imposter: in -> out (auth, session)`. Works around the streaming-harness limitation (LADR-05) that silently bypasses the in-band switch.

## Key Behaviors

- **Why a script, not an in-band trigger.** The router's `WhoMessageResponder` refuses to synthesize `--who?` replies when `stream:true` (LADR-05). Agent harnesses (Codex, Claude Code) stream by default, so a `--who?` typed into the harness forwards to the upstream and the real model answers it as a normal user turn. The skill's script issues a non-streaming request directly to the router, so the short-circuit fires.
- **Dialect selection.** Auto-detected from `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`; `--dialect` overrides. The base URLs point at the imposter router, not the real provider, so the probe is answered by the router itself with zero upstream HTTP calls.
- **Session identity.** The `--session` flag sends a `session_id` header. The router's `SessionIdentityResolver` reads headers before body fields, so sending the id as a header works for both dialects and for a prior `imposter-newsession` mapping to surface in the `session:` field.
- **No API key needed.** The router resolves upstream credentials from its own config, so the probe requires no `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`. The scripts forward a present key only in case an auth gate sits in front of the router.

## Coupling Hazards

- **LADR-05 reversal.** If HLD 010 ever reverses LADR-05 to synthesize SSE for streaming probes, the rationale for this skill's existence weakens but the skill remains valid (non-streaming curl is still the cheaper, portable probe path). Revisit the SKILL description if the in-band switch ever fires inside the harness.
- **Session header name.** `SessionIdentityResolver.HeaderCandidates` lists `session_id`, `x-opencode-session`, `x-session-id`, `conversation_id`. The script uses `session_id`; if that candidate is ever removed or reordered, the `--session` flag stops surfacing in the reply and the script needs to follow the new header name.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-25 | Initial version. | |
