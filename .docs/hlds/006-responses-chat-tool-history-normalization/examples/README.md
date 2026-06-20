# Examples — Responses Chat Tool History Normalization

These examples are illustrative payload shapes. They are not implementation instructions.

## Valid paired history survives

Responses input shape:

```json
[
  {
    "type": "function_call",
    "call_id": "call_1",
    "name": "exec_command",
    "arguments": "{\"cmd\":\"date\"}"
  },
  {
    "type": "function_call_output",
    "call_id": "call_1",
    "output": "Sat Jun 20 12:00:00 CEST 2026"
  },
  {
    "role": "user",
    "content": [{ "type": "input_text", "text": "continue" }]
  }
]
```

Chat history shape after downgrade:

```json
[
  {
    "role": "assistant",
    "tool_calls": [
      {
        "id": "call_1",
        "type": "function",
        "function": {
          "name": "exec_command",
          "arguments": "{\"cmd\":\"date\"}"
        }
      }
    ]
  },
  {
    "role": "tool",
    "tool_call_id": "call_1",
    "content": "Sat Jun 20 12:00:00 CEST 2026"
  },
  {
    "role": "user",
    "content": "continue"
  }
]
```

## Orphaned tool call is removed

Responses input shape:

```json
[
  {
    "type": "function_call",
    "call_id": "exec_command:0",
    "name": "exec_command",
    "arguments": "{\"cmd\":\"git diff\"}"
  },
  {
    "role": "user",
    "content": [{ "type": "input_text", "text": "review head to diff" }]
  }
]
```

Chat history shape after downgrade:

```json
[
  {
    "role": "user",
    "content": "review head to diff"
  }
]
```
