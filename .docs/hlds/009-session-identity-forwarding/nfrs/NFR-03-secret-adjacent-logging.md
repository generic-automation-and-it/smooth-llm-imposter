# NFR-03 — Secret-adjacent logging

- **Category:** Security / Observability
- **Status:** Accepted · 2026-07-24
- **Target:** Routing Information line includes `session=captured|derived|none` only; raw session values and fingerprint inputs never appear in logs.
- **Verification:** Router unit/integration coverage of log token; code review of logger calls.
