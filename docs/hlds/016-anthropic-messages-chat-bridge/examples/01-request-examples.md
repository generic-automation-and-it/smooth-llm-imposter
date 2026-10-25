# 01 - Request examples (TARGET output)

Companion to `01-request-mapping.md`. Each example shows the inbound Messages body (as it reaches the bridge: the router has already renamed `model`) and the resulting Chat Completions body, produced by profile `none` with default provider options (`sendReasoningEffort` false, `userIdMode` off) unless stated. The canonical key order of section 1 is shown. Text is never trimmed; the bridge adds no whitespace.

---------------------------------------------------------------------------------------------------

## Example 1: Claude Code first turn

Inbound model name known to the router: `claude-sonnet-4-5` (kept in the context, not in the body).

Messages request:

```json
{
  "model": "gpt-upstream",
  "max_tokens": 32000,
  "stream": true,
  "temperature": 1,
  "thinking": { "type": "enabled", "budget_tokens": 16000 },
  "metadata": { "user_id": "user_0f1e_account_9a8b_session_7c6d" },
  "system": [
    { "type": "text", "text": "x-anthropic-billing-header: cc_version=2.1.0.a1b; cc_entrypoint=cli;" },
    { "type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude.", "cache_control": { "type": "ephemeral" } },
    { "type": "text", "text": "You are an interactive CLI tool that helps users with software engineering tasks.\n", "cache_control": { "type": "ephemeral" } }
  ],
  "tools": [
    {
      "name": "Read",
      "description": "Reads a file from the local filesystem.",
      "input_schema": {
        "type": "object",
        "properties": { "file_path": { "type": "string" }, "limit": { "type": "number" } },
        "required": ["file_path"],
        "additionalProperties": false,
        "$schema": "http://json-schema.org/draft-07/schema#"
      }
    },
    {
      "name": "Bash",
      "description": "Executes a bash command.",
      "input_schema": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] },
      "cache_control": { "type": "ephemeral" }
    },
    { "type": "tool_search_tool_regex_20251119", "name": "tool_search_tool_regex" }
  ],
  "tool_choice": { "type": "auto", "disable_parallel_tool_use": false },
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "<system-reminder>\nAs you answer the user's questions, you can use the following context:\n# currentDate\nToday's date is 2026-10-24.\n</system-reminder>\n" },
        { "type": "text", "text": "fix the failing unit test in src/foo.test.ts", "cache_control": { "type": "ephemeral" } }
      ]
    }
  ]
}
```

Chat request. Notes: the first system block (attribution marker) is dropped (D18); remaining system blocks are joined with one `\n` and keep their own whitespace (the trailing `\n` of the last block stays); the two user text blocks are joined with one `\n` (the first block's own trailing `\n` plus the separator gives a blank line); the tool-search tool is silently removed; all `cache_control`, `thinking` and `metadata` keys are gone; `thinking` and `metadata` produce nothing under the default options (with `sendReasoningEffort` true the body would also end with `"reasoning_effort":"medium"`, derived from `budget_tokens` 16000; with `userIdMode` `passthrough` it would carry `"user":"user_0f1e_account_9a8b_session_7c6d"`); `disable_parallel_tool_use: false` emits nothing.

```json
{
  "model": "gpt-upstream",
  "messages": [
    { "role": "system", "content": "You are Claude Code, Anthropic's official CLI for Claude.\nYou are an interactive CLI tool that helps users with software engineering tasks.\n" },
    { "role": "user", "content": "<system-reminder>\nAs you answer the user's questions, you can use the following context:\n# currentDate\nToday's date is 2026-10-24.\n</system-reminder>\n\nfix the failing unit test in src/foo.test.ts" }
  ],
  "temperature": 1,
  "max_tokens": 32000,
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "Read",
        "parameters": {
          "type": "object",
          "properties": { "file_path": { "type": "string" }, "limit": { "type": "number" } },
          "required": ["file_path"],
          "additionalProperties": false,
          "$schema": "http://json-schema.org/draft-07/schema#"
        },
        "description": "Reads a file from the local filesystem."
      }
    },
    {
      "type": "function",
      "function": {
        "name": "Bash",
        "parameters": { "type": "object", "properties": { "command": { "type": "string" } }, "required": ["command"] },
        "description": "Executes a bash command."
      }
    }
  ],
  "tool_choice": "auto",
  "stream": true,
  "stream_options": { "include_usage": true }
}
```

Request-derived context: `{"inboundModel":"claude-sonnet-4-5","stream":true,"toolNameMap":[],"requestTokenEstimate":<estimator output>}`.

---------------------------------------------------------------------------------------------------

## Example 2: parallel tool calls, results listed in reverse order, sibling text, thinking history

Messages (other fields as needed):

```json
[
  { "role": "user", "content": "Find where Foo is defined and show the file header." },
  {
    "role": "assistant",
    "content": [
      { "type": "thinking", "thinking": "I should grep then read.", "signature": "" },
      { "type": "text", "text": "I'll search first.\n" },
      { "type": "tool_use", "id": "toolu_01A", "name": "Grep", "input": { "pattern": "class Foo", "output_mode": "files_with_matches" } },
      { "type": "tool_use", "id": "toolu_01B", "name": "Read", "input": { "file_path": "/repo/src/foo.ts", "limit": 20 } }
    ]
  },
  {
    "role": "user",
    "content": [
      { "type": "tool_result", "tool_use_id": "toolu_01B", "content": "     1\texport class Foo {}\n", "is_error": false },
      { "type": "tool_result", "tool_use_id": "toolu_01A", "content": [ { "type": "text", "text": "Found 1 file\n/repo/src/foo.ts" } ] },
      { "type": "text", "text": "<system-reminder>Todo list is empty.</system-reminder>", "cache_control": { "type": "ephemeral" } }
    ]
  }
]
```

Chat `messages` (profile `none`). Notes: thinking dropped; the assistant text keeps its trailing `\n` (no trimming); both calls in one assistant message; tool messages follow the assistant immediately and appear in the assistant's call order (A, then B) although the client listed B first (D14); the string result is verbatim; the sibling text becomes the user message after the tool messages.

```json
[
  { "role": "user", "content": "Find where Foo is defined and show the file header." },
  {
    "role": "assistant",
    "content": "I'll search first.\n",
    "tool_calls": [
      { "id": "toolu_01A", "type": "function", "function": { "name": "Grep", "arguments": "{\"pattern\":\"class Foo\",\"output_mode\":\"files_with_matches\"}" } },
      { "id": "toolu_01B", "type": "function", "function": { "name": "Read", "arguments": "{\"file_path\":\"/repo/src/foo.ts\",\"limit\":20}" } }
    ]
  },
  { "role": "tool", "tool_call_id": "toolu_01A", "content": "Found 1 file\n/repo/src/foo.ts" },
  { "role": "tool", "tool_call_id": "toolu_01B", "content": "     1\texport class Foo {}\n" },
  { "role": "user", "content": "<system-reminder>Todo list is empty.</system-reminder>" }
]
```

---------------------------------------------------------------------------------------------------

## Example 3: images (user image, image inside a tool result)

Messages:

```json
[
  {
    "role": "user",
    "content": [
      { "type": "text", "text": "What does this error dialog say?" },
      { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "iVBORw0KGgoAAAANSUhEUg==" } }
    ]
  },
  { "role": "assistant", "content": [ { "type": "tool_use", "id": "toolu_img1", "name": "Read", "input": { "file_path": "/tmp/shot.png" } } ] },
  {
    "role": "user",
    "content": [
      {
        "type": "tool_result",
        "tool_use_id": "toolu_img1",
        "content": [
          { "type": "text", "text": "Image file read." },
          { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "iVBORw0KGgoAAAANSUhEUg==" } }
        ]
      },
      { "type": "text", "text": "ok, and the text below it?" }
    ]
  }
]
```

Chat `messages`. Notes: a user image forces the parts-list form; the tool message carries only the text portion; the image is hoisted into a user message that directly follows the tool messages and starts with a text part naming the tool call (D15); the sibling text is a separate user message after it (adjacent user messages are not merged, D14).

```json
[
  {
    "role": "user",
    "content": [
      { "type": "text", "text": "What does this error dialog say?" },
      { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" } }
    ]
  },
  {
    "role": "assistant",
    "content": "",
    "tool_calls": [ { "id": "toolu_img1", "type": "function", "function": { "name": "Read", "arguments": "{\"file_path\":\"/tmp/shot.png\"}" } } ]
  },
  { "role": "tool", "tool_call_id": "toolu_img1", "content": "Image file read." },
  {
    "role": "user",
    "content": [
      { "type": "text", "text": "Images returned by tool call toolu_img1:" },
      { "type": "image_url", "image_url": { "url": "data:image/png;base64,iVBORw0KGgoAAAANSUhEUg==" } }
    ]
  },
  { "role": "user", "content": "ok, and the text below it?" }
]
```

An image-only result gives the tool message content `(tool returned an image; see the next message)`.

---------------------------------------------------------------------------------------------------

## Example 4: profile `deepseek` with thinking, effort and a tool-call turn

Provider options `compatProfile: deepseek`, `sendReasoningEffort: true` (without the second option the `thinking` and `reasoning_effort` keys below are not sent; the `reasoning_content` re-injection is unaffected).

Messages request:

```json
{
  "model": "deepseek-reasoner",
  "max_tokens": 8192,
  "stream": true,
  "thinking": { "type": "enabled", "budget_tokens": 4096 },
  "output_config": { "effort": "medium" },
  "system": "You are terse.",
  "tools": [
    { "name": "Read", "description": "Read a file", "input_schema": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] } }
  ],
  "messages": [
    { "role": "user", "content": "Open README and tell me the title." },
    {
      "role": "assistant",
      "content": [
        { "type": "thinking", "thinking": "I need to read README.md first.", "signature": "" },
        { "type": "tool_use", "id": "toolu_r1", "name": "Read", "input": { "file_path": "README.md" } }
      ]
    },
    { "role": "user", "content": [ { "type": "tool_result", "tool_use_id": "toolu_r1", "content": "# Project Atlas\nA thing." } ] }
  ]
}
```

Chat request. Notes: explicit effort `medium` maps to `high` for this vendor; `thinking` is `{"type":"enabled"}` (the budget number is not forwarded); the assistant turn re-injects the thinking text as `reasoning_content`; `content` is `""` because the turn has calls and no text.

```json
{
  "model": "deepseek-reasoner",
  "messages": [
    { "role": "system", "content": "You are terse." },
    { "role": "user", "content": "Open README and tell me the title." },
    {
      "role": "assistant",
      "content": "",
      "tool_calls": [ { "id": "toolu_r1", "type": "function", "function": { "name": "Read", "arguments": "{\"file_path\":\"README.md\"}" } } ],
      "reasoning_content": "I need to read README.md first."
    },
    { "role": "tool", "tool_call_id": "toolu_r1", "content": "# Project Atlas\nA thing." }
  ],
  "max_tokens": 8192,
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "Read",
        "parameters": { "type": "object", "properties": { "file_path": { "type": "string" } }, "required": ["file_path"] },
        "description": "Read a file"
      }
    }
  ],
  "stream": true,
  "stream_options": { "include_usage": true },
  "thinking": { "type": "enabled" },
  "reasoning_effort": "high"
}
```

The same request under profile `none`: the assistant message has no `reasoning_content` and there is no `thinking` key; `reasoning_effort` is `"medium"` only when `sendReasoningEffort` is true, otherwise absent.

---------------------------------------------------------------------------------------------------

## Example 5: tool names, collisions, forced tool, tool_reference result, error result

Messages request:

```json
{
  "model": "m",
  "max_tokens": 1024,
  "tool_choice": { "type": "tool", "name": "my tool.v2", "disable_parallel_tool_use": true },
  "tools": [
    { "name": "mcp__github__create_issue", "description": "Create an issue", "input_schema": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] } },
    { "name": "my tool.v2", "description": "", "defer_loading": true, "input_schema": { "type": "object", "properties": {} } },
    { "name": "my_tool_v2", "strict": true, "input_schema": { "type": "object" } },
    { "name": "mcp__averyverylongserver__averyverylongtoolname_that_goes_past_the_limit_of_64", "input_schema": { "type": "object", "properties": {} } },
    { "type": "tool_search_tool_regex_20251119", "name": "tool_search_tool_regex" },
    { "name": "日本語", "input_schema": { "type": "object", "properties": {} } }
  ],
  "messages": [
    { "role": "user", "content": "go" },
    {
      "role": "assistant",
      "content": [
        { "type": "tool_use", "id": "toolu_9", "name": "my tool.v2", "input": {} },
        { "type": "tool_use", "id": "toolu_10", "name": "unlisted tool!", "input": { "a": 1 } }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "toolu_9", "content": [ { "type": "tool_reference", "tool_name": "my tool.v2" }, { "type": "tool_reference", "tool_name": "mcp__github__create_issue" } ] },
        { "type": "tool_result", "tool_use_id": "toolu_10", "content": "ok", "is_error": true }
      ]
    }
  ]
}
```

Chat request. Notes: runs of illegal characters become one `_`, existing underscores are kept; `my_tool_v2` collides with the sanitised `my tool.v2`, so it gets the first 8 hex of SHA-256 of its own original name; the 78-character name is cut to 55 characters plus `_` plus 8 hex; `日本語` becomes `_`; the tool-search tool is removed; `defer_loading`, `strict` and the empty description are gone; the history-only tool `unlisted tool!` is mapped by the same rule (trailing underscore kept); `tool_choice` uses the mapped name; with tools present and `disable_parallel_tool_use` true, `parallel_tool_calls` is `false`; the tool_reference blocks become a JSON line of mapped names; the error result is prefixed with `Error: `.

```json
{
  "model": "m",
  "messages": [
    { "role": "user", "content": "go" },
    {
      "role": "assistant",
      "content": "",
      "tool_calls": [
        { "id": "toolu_9", "type": "function", "function": { "name": "my_tool_v2", "arguments": "{}" } },
        { "id": "toolu_10", "type": "function", "function": { "name": "unlisted_tool_", "arguments": "{\"a\":1}" } }
      ]
    },
    { "role": "tool", "tool_call_id": "toolu_9", "content": "[{\"type\":\"tool_reference\",\"tool_name\":\"my_tool_v2\"},{\"type\":\"tool_reference\",\"tool_name\":\"mcp__github__create_issue\"}]" },
    { "role": "tool", "tool_call_id": "toolu_10", "content": "Error: ok" }
  ],
  "max_tokens": 1024,
  "tools": [
    { "type": "function", "function": { "name": "mcp__github__create_issue", "parameters": { "type": "object", "properties": { "title": { "type": "string" } }, "required": ["title"] }, "description": "Create an issue" } },
    { "type": "function", "function": { "name": "my_tool_v2", "parameters": { "type": "object", "properties": {} } } },
    { "type": "function", "function": { "name": "my_tool_v2_c7ba60b6", "parameters": { "type": "object" } } },
    { "type": "function", "function": { "name": "mcp__averyverylongserver__averyverylongtoolname_that_go_3609e2a4", "parameters": { "type": "object", "properties": {} } } },
    { "type": "function", "function": { "name": "_", "parameters": { "type": "object", "properties": {} } } }
  ],
  "tool_choice": { "type": "function", "function": { "name": "my_tool_v2" } },
  "parallel_tool_calls": false
}
```

Request-derived context for this example: `toolNameMap` = (`my tool.v2` -> `my_tool_v2`), (`my_tool_v2` -> `my_tool_v2_c7ba60b6`), (`mcp__averyverylongserver__averyverylongtoolname_that_goes_past_the_limit_of_64` -> `mcp__averyverylongserver__averyverylongtoolname_that_go_3609e2a4`), (`日本語` -> `_`), (`unlisted tool!` -> `unlisted_tool_`); identity pairs omitted.

---------------------------------------------------------------------------------------------------

## Example 6: orphan results, unanswered calls, reasoning-only turn

6a. Orphan result (the preceding assistant message made no calls):

```json
[
  { "role": "user", "content": "hi" },
  { "role": "assistant", "content": "ok" },
  { "role": "user", "content": [ { "type": "tool_result", "tool_use_id": "X", "content": "done" } ] }
]
```

Chat `messages`: `[{"role":"user","content":"hi"},{"role":"assistant","content":"ok"},{"role":"user","content":"Tool result (tool_use_id: X):\ndone"}]` - no tool message is emitted (D14).

6b. Unanswered call (kept as is; the upstream decides):

```json
[
  { "role": "user", "content": "run it" },
  { "role": "assistant", "content": [ { "type": "tool_use", "id": "T1", "name": "Bash", "input": { "command": "ls" } } ] },
  { "role": "user", "content": "never mind" }
]
```

Chat `messages`: user "run it"; assistant `{"role":"assistant","content":"","tool_calls":[{"id":"T1","type":"function","function":{"name":"Bash","arguments":"{\"command\":\"ls\"}"}}]}`; user "never mind".

6c. One matched result, one orphan, sibling text:

```json
[
  { "role": "user", "content": "go" },
  { "role": "assistant", "content": [
      { "type": "tool_use", "id": "A", "name": "Read", "input": {} },
      { "type": "tool_use", "id": "B", "name": "Read", "input": {} } ] },
  { "role": "user", "content": [
      { "type": "tool_result", "tool_use_id": "A", "content": "x" },
      { "type": "tool_result", "tool_use_id": "Z", "content": "y" },
      { "type": "text", "text": "t" } ] }
]
```

Chat `messages`: user "go"; assistant with calls A and B; `{"role":"tool","tool_call_id":"A","content":"x"}`; user `"Tool result (tool_use_id: Z):\ny\nt"` (orphan text and sibling text are consecutive text parts, joined by one `\n`). Call B stays unanswered.

6d. Reasoning-only assistant turn under profile `none`:

```json
[
  { "role": "user", "content": "a" },
  { "role": "assistant", "content": [ { "type": "thinking", "thinking": "t", "signature": "" } ] },
  { "role": "user", "content": "b" }
]
```

Chat `messages`: user "a", user "b" (the assistant turn is removed; the two user messages are not merged).

---------------------------------------------------------------------------------------------------

## Example 7: one request through every profile

Messages request (no stream, no tools):

```json
{
  "model": "m",
  "max_tokens": 2048,
  "thinking": { "type": "enabled", "budget_tokens": 4096 },
  "output_config": { "effort": "medium" },
  "messages": [
    { "role": "user", "content": "hi" },
    { "role": "assistant", "content": [ { "type": "thinking", "thinking": "t", "signature": "" }, { "type": "text", "text": "hello" } ] },
    { "role": "user", "content": "bye" }
  ]
}
```

Common: `messages[0]` user "hi", `messages[2]` user "bye", `max_tokens: 2048`. All rows assume `sendReasoningEffort: true` except where stated. Differences after `max_tokens`, in canonical order:

| Profile | Extra keys | messages[1] |
|---|---|---|
| `none` | `"reasoning_effort":"medium"` | `{"role":"assistant","content":"hello"}` |
| `none`, default `sendReasoningEffort` (false) | none | `{"role":"assistant","content":"hello"}` |
| `deepseek`, default `sendReasoningEffort` (false) | none | `{"role":"assistant","content":"hello","reasoning_content":"t"}` |
| `minimax`, default `sendReasoningEffort` (false) | `"reasoning_split":true` | `{"role":"assistant","content":"hello","reasoning_details":[{"type":"reasoning.text","index":0,"text":"t"}]}` |
| `deepseek` | `"thinking":{"type":"enabled"},"reasoning_effort":"high"` | `{"role":"assistant","content":"hello","reasoning_content":"t"}` |
| `minimax` | `"reasoning_split":true` | `{"role":"assistant","content":"hello","reasoning_details":[{"type":"reasoning.text","index":0,"text":"t"}]}` |
| `mimo` | `"thinking":{"type":"enabled"}` | `{"role":"assistant","content":"hello","reasoning_content":"t"}` |
| `zhipu` | `"thinking":{"type":"enabled"},"reasoning_effort":"medium"` | `{"role":"assistant","content":"hello"}` |

---------------------------------------------------------------------------------------------------

## Example 8: unsupported content and rejected tool definitions

8a. Unsupported blocks become placeholder text (no error). Messages:

```json
[
  {
    "role": "user",
    "content": [
      { "type": "text", "text": "Summarize the attached spec." },
      { "type": "document", "title": "spec.pdf", "source": { "type": "base64", "media_type": "application/pdf", "data": "JVBERi0xLjQK" } }
    ]
  },
  { "role": "assistant", "content": [ { "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "x" } } ] },
  { "role": "user", "content": [ { "type": "web_search_tool_result", "tool_use_id": "srvtoolu_1", "content": [] } ] }
]
```

Chat `messages`: the placeholder merges with the adjacent text of the same message; a message whose only content was an unsupported block keeps the placeholder (it is not removed, so role order is preserved); each replacement is logged at debug.

```json
[
  { "role": "user", "content": "Summarize the attached spec.\n[omitted: unsupported content block 'document']" },
  { "role": "assistant", "content": "[omitted: unsupported content block 'server_tool_use']" },
  { "role": "user", "content": "[omitted: unsupported content block 'web_search_tool_result']" }
]
```

8b. A document inside a tool result becomes a line of the tool message text:

```json
{ "type": "tool_result", "tool_use_id": "toolu_7", "content": [ { "type": "text", "text": "x" }, { "type": "document", "source": { "type": "text", "media_type": "text/plain", "data": "y" } } ] }
```

Tool message content: `"x\n[omitted: unsupported content block 'document']"`.

8c. A typed server tool definition is rejected (HTTP 400, Messages error envelope), naming the tool type at its index:

```json
{ "type": "error", "error": { "type": "invalid_request_error", "message": "tools.2.type: \"web_search_20250305\" server tools are not supported" } }
```

8d. A forced tool without a name:

```json
{ "type": "error", "error": { "type": "invalid_request_error", "message": "tool_choice.name: Field required" } }
```

8e. A forced choice with no usable tool (`{"type":"any"}`, no tools):

```json
{ "type": "error", "error": { "type": "invalid_request_error", "message": "tool_choice: \"any\" requires at least one tool" } }
```

8f. Probe-sized `max_tokens` is not an error: `"max_tokens": 1` yields `"max_tokens": 16` in the Chat body (D19).
