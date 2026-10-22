# NFR-01: Disabled and no-drop body identity

**Status:** Accepted

## Requirement

Disabled filtering returns before body parsing, materialization, classifier access or gate allocation. Non-allowlisted paths also return before parsing. A skipped, wholly failed or no-drop pass returns the original string instance; reserialize only when a tool is removed and preserve catalog order.

## Verification

L0 uses malformed JSON, reference identity, a decision-client spy and captured logs. L2 captures exact upstream bytes on default-off passthrough routes with tools.

## Acceptance Criteria

- Malformed bodies remain untouched when disabled or on an excluded path.
- No classifier calls or logs above Debug occur on disabled passes.
- No-drop passes preserve reference identity; filtered catalogs preserve order.

## Applies To

All supported API shapes and both route kinds.
