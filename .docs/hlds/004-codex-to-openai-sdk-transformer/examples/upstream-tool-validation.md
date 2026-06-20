# Eval — strict upstream tool validation

**Target:** `opencode → Moonshot kimi-k2.7-code` via `POST https://opencode.ai/zen/go/v1/chat/completions`
**Date:** 2026-06-20

Purpose: characterize what the strict upstream rejects in the `tools` array so the (future)
normalization transform's rules are derived from observed behavior, not assumption. Every request
uses identical headers; **only the body's `tools` differs**. The headers (incl. `originator` /
`user-agent`) were independently shown to be irrelevant — `openai_sdk` and `codex_sdk_ts` both
return 200 with no tools.

## Reusable request shell

```bash
curl -sS -w '\n[%{http_code}]\n' -X POST https://opencode.ai/zen/go/v1/chat/completions \
  -H 'authorization: Bearer sk-<key>' -H 'content-type: application/json' \
  -H 'originator: codex_sdk_ts' -H 'user-agent: codex_sdk_ts/0.138.0' \
  --data '<BODY>'
```

All bodies share: `"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}]`.

## Negative cases — observed `400 invalid_request_error`

| # | `tools` entry (the variable) | Observed error message | Rule proven |
|---|------------------------------|------------------------|-------------|
| 1 | `function` name `multi_tool_use.parallel` | `function name is invalid, must start with a letter and can contain letters, numbers, underscores, and dashes` | name may not contain `.` (charset `[A-Za-z0-9_-]`) |
| 2 | `type:"custom"` (apply_patch) | `unknown tool type: custom, currently only function and plugin are supported` | type must be `function`/`plugin` |
| 3 | `type:"namespace"` (mcp wrapper) | `unknown tool type: namespace, …` | type must be `function`/`plugin` |
| 4 | `type:"web_search"` | `unknown tool type: web_search, …` | type must be `function`/`plugin` |
| 5 | `type:"image_generation"` | `unknown tool type: image_generation, …` | type must be `function`/`plugin` |
| 6 | `type:"tool_search"` | `unknown tool type: tool_search, …` | type must be `function`/`plugin` |
| 7 | `function` name `""` (empty) | `function name is invalid, must start with a letter …` | name must be non-empty |

Bodies (paste into `<BODY>`):

```json
1  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"multi_tool_use.parallel","parameters":{"type":"object","properties":{}}}}]}
2  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"custom","name":"apply_patch","description":"Apply a patch."}]}
3  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"namespace","name":"mcp__codex_apps__github","tools":[{"type":"function","name":"_search_issues","parameters":{"type":"object","properties":{}}}]}]}
4  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"web_search","external_web_access":true}]}
5  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"image_generation","output_format":"png"}]}
6  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"tool_search","execution":"client"}]}
7  {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"","parameters":{"type":"object","properties":{}}}}]}
```

## Positive cases

`[obs]` = observed; `[run]` = proposed, not yet run.

| # | `tools` entry | Expected | Purpose |
|---|---------------|----------|---------|
| P1 | none | 200 `[obs]` | baseline — no tools |
| P2 | `function` name `search_issues` | 200 `[obs]` | valid letter-initial name accepted (with reasoning over tools) |
| P3 | 4× `function` names `_search_issues`,`_create_pull_request`,`_fetch_pr`,`_update_file` | 200 `[obs]` | **leading underscore is tolerated** despite the "must start with a letter" wording |
| P4 | `function` name `get-issue-2` | 200 `[obs]` | digits + dash mid-name accepted |
| P5 | `function` name `2fa_check` (leading digit) | **400** `[obs]` | leading digit **rejected** → name must start with a letter or `_` (the message's "start with a letter" is enforced for digits, not for `_`) |
| P6 | `type:"plugin"` (minimal `{type,name}`) | **400** `[obs]` | `unknown tool type: plugin` — the minimal shape is rejected though the message lists `plugin` as supported; the `plugin` schema is uncharacterized (TBD). v1 uses `function` only |
| P7 | Real toolset **after normalization** (only valid `function` tools incl. a `_`-name) | **200** `[obs]` | **end-to-end proof the v1 transform output is accepted** |

Positive bodies (paste into `<BODY>`):

```json
P1 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}]}
P2 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"search_issues","parameters":{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}}}]}
P3 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"_search_issues","parameters":{"type":"object","properties":{}}}},{"type":"function","function":{"name":"_create_pull_request","parameters":{"type":"object","properties":{}}}},{"type":"function","function":{"name":"_fetch_pr","parameters":{"type":"object","properties":{}}}},{"type":"function","function":{"name":"_update_file","parameters":{"type":"object","properties":{}}}}]}
P4 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"get-issue-2","parameters":{"type":"object","properties":{}}}}]}
P5 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"2fa_check","parameters":{"type":"object","properties":{}}}}]}
P6 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"plugin","name":"example_plugin"}]}
P7 {"model":"kimi-k2.7-code","messages":[{"role":"user","content":"Hi"}],"tools":[{"type":"function","function":{"name":"exec_command","parameters":{"type":"object","properties":{}}}},{"type":"function","function":{"name":"_search_issues","parameters":{"type":"object","properties":{}}}}]}
```

## Observed upstream contract (kimi-k2.7-code)

- `tools[].type` ∈ {`function`, `plugin`}; any other type → `unknown tool type`. In practice only
  `function` is usable: a minimal `plugin` entry is itself rejected (P6) and its schema is unknown (TBD).
- `tools[].function.name`: required, non-empty, matches **`^[A-Za-z_][A-Za-z0-9_-]*$`** — must start
  with a **letter or underscore** (leading `_` accepted, P3; leading **digit rejected**, P5), then
  letters/digits/`_`/`-`; **no dots** (negative #1) or other punctuation; must be **non-empty** (negative #7).
- Request identity headers (`originator`, `user-agent`) do not affect acceptance.

## Implications for normalization (request-only; see LADR-02)

Derived rules for the future transform (no rename → no response remap):

1. **Drop every tool whose `type` ∉ {`function`, `plugin`}.** Covers Codex's `custom`, `namespace`,
   `web_search`, `image_generation`, `tool_search`. Optional capability-preserving refinement:
   **flatten** `type:"namespace"` into its nested `type:"function"` entries (their names, incl.
   `_`-prefixed, are accepted) instead of dropping the whole wrapper.
2. **Drop (or repair) any `function` whose name is empty or contains characters outside
   `[A-Za-z0-9_-]`** (e.g. dotted names). Dropping keeps the transform one-directional.
3. **Clean `tool_choice`** that references a removed tool.

These are observations + implications, not the design. The transform's exact rule set is specced at
implementation time (worktask), within the seam/policy this HLD defines.
