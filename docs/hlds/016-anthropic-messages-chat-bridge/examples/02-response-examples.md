# 02 - Response mapping: worked examples (target output only)

Companion to `02-response-mapping.md`. Each example shows the upstream reply and the exact target output. Conventions: inbound model `claude-sonnet-4-5`; request token estimate R = 1500 (only used where the upstream sends no usage); `msg_<24>` and `toolu_<24>` stand for 24 random characters from `[0-9A-Za-z]`. Output JSON is compact on the wire; it is shown indented here for reading. Token figures that use the shared estimator assume a `ceil(characters / 4)` estimator purely for illustration; the real estimator is defined in `04-harness-and-errors.md`.

---

## Example 1 - Plain text, preserved whitespace, prompt-cache hit

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-9fK2",
  "object": "chat.completion",
  "created": 1760000000,
  "model": "gpt-4o-2024-08-06",
  "system_fingerprint": "fp_44709d6fcb",
  "choices": [
    { "index": 0, "message": { "role": "assistant", "content": "The build passed.\n" }, "finish_reason": "stop" }
  ],
  "usage": {
    "prompt_tokens": 1200,
    "completion_tokens": 6,
    "total_tokens": 1206,
    "prompt_tokens_details": { "cached_tokens": 1024 },
    "completion_tokens_details": { "reasoning_tokens": 0 }
  }
}
```

Target (HTTP 200, `content-type: application/json`)
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [ { "type": "text", "text": "The build passed.\n" } ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": { "input_tokens": 176, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 1024, "output_tokens": 6 }
}
```
Rules shown: id and model are the bridge's, not the upstream's; text is byte-for-byte (trailing newline kept); `input_tokens = 1200 - 1024`.

---

## Example 2 - Tool-call turn: text, two parallel calls, illegal vendor id, missing id, tool-name reverse map

Context: name map `{"mcp__my_srv__read": "mcp__my.srv__read"}`.

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-ab12",
  "model": "kimi-k2",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "I'll read both files.",
        "tool_calls": [
          { "id": "functions.Read:0", "type": "function",
            "function": { "name": "Read", "arguments": "{\"file_path\":\"/a.txt\"}" } },
          { "type": "function",
            "function": { "name": "mcp__my_srv__read", "arguments": "{\"file_path\":\"/b.txt\",\"limit\":1.0}" } }
        ]
      },
      "finish_reason": "tool_calls"
    }
  ],
  "usage": { "prompt_tokens": 500, "completion_tokens": 40, "total_tokens": 540 }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [
    { "type": "text", "text": "I'll read both files." },
    { "type": "tool_use", "id": "toolu_<24>", "name": "Read", "input": { "file_path": "/a.txt" } },
    { "type": "tool_use", "id": "toolu_<24>", "name": "mcp__my.srv__read", "input": { "file_path": "/b.txt", "limit": 1.0 } }
  ],
  "stop_reason": "tool_use",
  "stop_sequence": null,
  "usage": { "input_tokens": 500, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 40 }
}
```
Rules shown: text first, then calls in order; `functions.Read:0` is illegal (contains `.` and `:`) so both ids are synthesized and distinct; the sanitized upstream name is mapped back to the client's original; `1.0` keeps its textual form.

---

## Example 3 - Plain `reasoning_content` with vendor cache counters

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-r1",
  "model": "deepseek-reasoner",
  "choices": [
    {
      "index": 0,
      "message": { "role": "assistant", "reasoning_content": "The user wants 2+2. That is 4.", "content": "4" },
      "finish_reason": "stop"
    }
  ],
  "usage": {
    "prompt_tokens": 120,
    "completion_tokens": 30,
    "total_tokens": 150,
    "prompt_cache_hit_tokens": 80,
    "prompt_cache_miss_tokens": 40,
    "completion_tokens_details": { "reasoning_tokens": 24 }
  }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [
    { "type": "thinking", "thinking": "The user wants 2+2. That is 4.", "signature": "" },
    { "type": "text", "text": "4" }
  ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": { "input_tokens": 40, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 80, "output_tokens": 30 }
}
```
Rules shown: thinking precedes text and has an empty signature; the vendor cache-hit counter is the cache-read value (no standard field was present); `input_tokens = 120 - 80`.

---

## Example 4 - `reasoning_details` with signature and encrypted entries, duplicated by a plain field

Upstream (HTTP 200)
```json
{
  "id": "gen-123",
  "model": "anthropic/claude-sonnet-4.5",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Done.",
        "reasoning": "Plan: check the file.",
        "reasoning_details": [
          { "type": "reasoning.text", "text": "Plan: check the file.", "signature": "EqQBCkYI...", "format": "anthropic-claude-v1", "index": 0 },
          { "type": "reasoning.encrypted", "data": "gAAAAB...", "id": "rs_abc", "format": "openai-responses-v1", "index": 1 }
        ]
      },
      "finish_reason": "stop"
    }
  ],
  "usage": { "prompt_tokens": 80, "completion_tokens": 25, "total_tokens": 105 }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [
    { "type": "thinking", "thinking": "Plan: check the file.", "signature": "" },
    { "type": "text", "text": "Done." }
  ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": { "input_tokens": 80, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 25 }
}
```
Rules shown: the plain `reasoning` equals the details text, so one block results; the upstream signature is discarded; the encrypted entry is dropped without error and no `redacted_thinking` block is produced.

---

## Example 5 - `finish_reason: "stop"` although a tool call is present

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-ol1",
  "model": "qwen2.5-coder:32b",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "",
        "tool_calls": [
          { "id": "call_a1b2", "type": "function", "function": { "name": "Bash", "arguments": "{\"command\":\"ls\"}" } }
        ]
      },
      "finish_reason": "stop"
    }
  ],
  "usage": { "prompt_tokens": 300, "completion_tokens": 18 }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [
    { "type": "tool_use", "id": "call_a1b2", "name": "Bash", "input": { "command": "ls" } }
  ],
  "stop_reason": "tool_use",
  "stop_sequence": null,
  "usage": { "input_tokens": 300, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 18 }
}
```
Rules shown: empty content yields no text block; the id `call_a1b2` is legal and unique so it is kept; a tool_use block forces `tool_use` regardless of `finish_reason`.

---

## Example 6 - Output cut off mid tool call (`finish_reason: "length"`)

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-len1",
  "model": "qwen3-coder",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "I'll write the parser now.",
        "tool_calls": [
          { "id": "call_9", "type": "function",
            "function": { "name": "Write", "arguments": "{\"file_path\":\"/a.py\",\"content\":" } }
        ]
      },
      "finish_reason": "length"
    }
  ],
  "usage": { "prompt_tokens": 900, "completion_tokens": 4096 }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [ { "type": "text", "text": "I'll write the parser now." } ],
  "stop_reason": "max_tokens",
  "stop_sequence": null,
  "usage": { "input_tokens": 900, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 4096 }
}
```
Rules shown: `{"file_path":"/a.py","content":` ends after a key with no value, which repair may not invent, so the arguments stay invalid; because the finish reason is a length reason the block is dropped (no `{}` substitution); no tool_use remains, so `max_tokens`. Variant: if an earlier call in the same message had valid arguments it would be kept and `stop_reason` would be `tool_use`.

---

## Example 7 - Refusal text and no usage from the upstream (estimated usage)

Context: request token estimate R = 1500.

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-ref1",
  "model": "gpt-4.1",
  "choices": [
    {
      "index": 0,
      "message": { "role": "assistant", "content": null, "refusal": "I can't help with that." },
      "finish_reason": "content_filter"
    }
  ]
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [ { "type": "text", "text": "I can't help with that." } ],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": { "input_tokens": 1500, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 6 }
}
```
Rules shown: refusal text becomes assistant text; `content_filter` maps to `end_turn` (never a refusal stop reason); with no `usage` the prompt count is the request estimate and the output count is the estimator applied to the emitted text (23 characters -> 6 under the illustrative estimator).

---

## Example 8 - Empty completion (faithful, not an error)

Upstream (HTTP 200)
```json
{
  "id": "chatcmpl-empty",
  "model": "gpt-4o-mini",
  "choices": [ { "index": 0, "message": { "role": "assistant", "content": "" }, "finish_reason": "stop" } ],
  "usage": { "prompt_tokens": 50, "completion_tokens": 0, "total_tokens": 50 }
}
```

Target
```json
{
  "id": "msg_<24>",
  "type": "message",
  "role": "assistant",
  "model": "claude-sonnet-4-5",
  "content": [],
  "stop_reason": "end_turn",
  "stop_sequence": null,
  "usage": { "input_tokens": 50, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0, "output_tokens": 0 }
}
```
Rules shown: no synthetic 502, no second upstream request; `content: []`; the reported usage is used (completion 0 is kept because the message has no output).

---

## Example 9 - Upstream rate limit

Upstream: HTTP 429, header `Retry-After: 7`
```json
{ "error": { "message": "Rate limit reached for model in organization org-xyz on tokens per min.", "type": "tokens", "code": "rate_limit_exceeded" } }
```

Target: HTTP 429, header `retry-after: 7`
```json
{
  "type": "error",
  "error": {
    "type": "rate_limit_error",
    "message": "Rate limit reached for model in organization org-xyz on tokens per min."
  }
}
```

---

## Example 10 - Context-length overflow and invalid tool arguments

Upstream: HTTP 400
```json
{ "error": { "code": "context_length_exceeded", "message": "This model's maximum context length is 128000 tokens. However, your messages resulted in 131500 tokens." } }
```
Target: HTTP 400; the message wording is the one fixed by `04-harness-and-errors.md` and starts with `prompt is too long`.
```json
{
  "type": "error",
  "error": { "type": "invalid_request_error", "message": "prompt is too long: <wording per 04>" }
}
```

Upstream: HTTP 200, `finish_reason: "tool_calls"`, one call named `Edit` with `arguments` equal to the JSON string `[1,2]` (valid JSON, not an object; not repairable).
Target: HTTP 502
```json
{
  "type": "error",
  "error": { "type": "api_error", "message": "Upstream returned invalid arguments for tool call \"Edit\"." }
}
```
Rules shown: overflow overrides the status/type table and always yields 400 `invalid_request_error`; unrepairable arguments on a non-length finish fail the message with 502 `api_error` and never become `{}`.
