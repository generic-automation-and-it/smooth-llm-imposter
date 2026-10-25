# NFR-04: Every reply is a valid Messages reply

**Status:** Accepted

## Requirement

Every non-error stream satisfies the Messages event grammar: `message_start`, then zero or more blocks with dense increasing indexes that are opened and closed one at a time, then `message_delta` with a stop reason and non-empty usage, then `message_stop`. Every failure ends the stream with a Messages `error` event. No stream ends silently or with a frame after its terminal event. Every non-stream reply is a Messages message or the Messages error envelope.

## Verification

A grammar checker applied to every stream-spec matrix transcript and to replays of recorded upstream streams, including truncated and malformed ones. A client-compatibility run drives a real Claude Code session through a stub upstream.

## Acceptance Criteria

- Zero grammar violations across matrix and replays.
- Truncated, malformed and in-band-error upstream streams all end with an error event.
- The Claude Code session completes a tool-using turn and a long-reasoning turn without client-side protocol errors.

## Applies To

Stream and non-stream response mapping, the error envelope ([LADR-05](../ladrs/LADR-05-messages-shaped-errors-and-usage.md)).
