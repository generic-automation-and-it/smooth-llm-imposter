# 03 - Streaming worked transcripts (TARGET output only)

Companion to `03-stream-state-machine.md`. Each transcript shows the upstream Chat Completions SSE lines and the exact Messages SSE the bridge writes.

Context for every example: inbound model `claude-sonnet-4-5`; request input estimate E = 1234; synthesized message ids are 24 characters after `msg_`. Frames are shown with a blank line between events, as on the wire. Upstream chunks within one example share `id`, `object`, `created` and `model`; those values never appear in the output.

---

## Example 1: plain text, usage in a separate final chunk

Input:

```text
data: {"id":"chatcmpl-A1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}],"usage":null}

data: {"id":"chatcmpl-A1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Hel"},"finish_reason":null}],"usage":null}

data: {"id":"chatcmpl-A1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"lo!"},"finish_reason":null}],"usage":null}

data: {"id":"chatcmpl-A1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

data: {"id":"chatcmpl-A1","object":"chat.completion.chunk","created":1,"model":"gpt-x","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15,"prompt_tokens_details":{"cached_tokens":0}}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_01AbCdEfGhIjKlMnOpQrStUv","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lo!"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":12,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":3}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: chunk 1 writes message_start and ping; chunk 4 only latches the finish (no frames); chunk 5 carries usage after the finish, so the message finalizes there and `[DONE]` is never read.

---

## Example 2: text, then two tool calls with fragmented arguments, cached tokens

Input:

```text
data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"content":"I'll check both."},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_weather","arguments":""}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"city\":"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"Paris\"}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"get_time","arguments":""}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"tz\":\"CET\"}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

data: {"id":"chatcmpl-B2","object":"chat.completion.chunk","created":2,"model":"m-1","choices":[],"usage":{"prompt_tokens":200,"completion_tokens":40,"total_tokens":240,"prompt_tokens_details":{"cached_tokens":150}}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_02BcDeFgHiJkLmNoPqRsTuVw","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"I'll check both."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call_a","name":"get_weather","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":\"Paris\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: content_block_start
data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"call_b","name":"get_time","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"tz\":\"CET\"}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":2}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":50,"cache_creation_input_tokens":0,"cache_read_input_tokens":150,"output_tokens":40}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: chunk 3 starts the first call and closes the text block (block type switch); chunks 3-5 write nothing (arguments are held). Chunk 6 starts a different call while the first call's held arguments form a complete object, so the first call is validated and written as one burst (block 1). Chunk 8 (finish) completes the second call (block 2). Chunk 9 supplies usage: input 200 minus cached 150 = 50. The blocks never overlap.

---

## Example 3: reasoning, then text; finish and usage in the same final chunk

Input:

```text
data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"role":"assistant","content":null,"reasoning_content":""},"finish_reason":null}]}

data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"content":null,"reasoning_content":"The user is greeting me."},"finish_reason":null}]}

data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"content":null,"reasoning_content":" Reply briefly."},"finish_reason":null}]}

data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"content":"Hi","reasoning_content":null},"finish_reason":null}]}

data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"content":" there!","reasoning_content":null},"finish_reason":null}]}

data: {"id":"ds-1","object":"chat.completion.chunk","created":3,"model":"ds-r","choices":[{"index":0,"delta":{"content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":9,"completion_tokens":14,"total_tokens":23}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_03CdEfGhIjKlMnOpQrStUvWx","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"The user is greeting me."}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":" Reply briefly."}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hi"}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":" there!"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":9,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":14}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: the empty reasoning string and null content in chunk 1 open no block. The thinking block carries an empty signature and no `signature_delta` is written. The text delta closes the thinking block.

---

## Example 4: output cut by the token limit (text)

Input:

```text
data: {"id":"chatcmpl-D4","object":"chat.completion.chunk","created":4,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-D4","object":"chat.completion.chunk","created":4,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"The quick brown "},"finish_reason":null}]}

data: {"id":"chatcmpl-D4","object":"chat.completion.chunk","created":4,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"fox jumps"},"finish_reason":null}]}

data: {"id":"chatcmpl-D4","object":"chat.completion.chunk","created":4,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"length"}]}

data: {"id":"chatcmpl-D4","object":"chat.completion.chunk","created":4,"model":"gpt-x","choices":[],"usage":{"prompt_tokens":30,"completion_tokens":16,"total_tokens":46}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_04DeFgHiJkLmNoPqRsTuVwXy","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The quick brown "}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"fox jumps"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"input_tokens":30,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":16}}

event: message_stop
data: {"type":"message_stop"}

```

---

## Example 5a: output cut by the token limit inside a tool call (the call is dropped)

Input:

```text
data: {"id":"chatcmpl-E5","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-E5","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_w","type":"function","function":{"name":"write_file","arguments":"{\"path\":\"/tmp/a.txt\",\"content\":\"hel"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-E5","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"length"}],"usage":{"prompt_tokens":55,"completion_tokens":64,"total_tokens":119}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_05EfGhIjKlMnOpQrStUvWxYz","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"max_tokens","stop_sequence":null},"usage":{"input_tokens":55,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":64}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: the call was still pending when the stream ended by the length limit and its arguments are not a complete JSON object, so it is dropped without repair and nothing is written for it. No tool_use block was emitted, so the stop reason is `max_tokens`; the message content is empty.

---

## Example 5b: length stop after one complete call and one cut-off call

Input:

```text
data: {"id":"chatcmpl-E6","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-E6","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"p\":1}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-E6","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_2","type":"function","function":{"name":"write_file","arguments":"{\"path\":\"/tmp/b.txt\",\"content\":\"ab"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-E6","object":"chat.completion.chunk","created":5,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"length"}],"usage":{"prompt_tokens":60,"completion_tokens":80,"total_tokens":140}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_06FgHiJkLmNoPqRsTuVwXyZa","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_1","name":"read_file","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"p\":1}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":60,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":80}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: call 1 completed when call 2 started and was written then. Call 2 was cut off by the length limit and is dropped. A complete tool_use was emitted, so the stop reason is `tool_use` (D1).

---

## Example 6a: in-band error event from the upstream, mid-stream

Input:

```text
data: {"id":"chatcmpl-F6","object":"chat.completion.chunk","created":6,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-F6","object":"chat.completion.chunk","created":6,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Par"},"finish_reason":null}]}

event: error
data: {"error":{"type":"overloaded_error","message":"Upstream is busy"}}

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_07GhIjKlMnOpQrStUvWxYzAb","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Par"}}

event: error
data: {"type":"error","error":{"type":"overloaded_error","message":"Upstream is busy"}}

```

Notes: the response ends after the error frame: no content_block_stop, no message_delta, no message_stop.

## Example 6b: bare error object in an ordinary data line

Input:

```text
data: {"id":"chatcmpl-G6","object":"chat.completion.chunk","created":6,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-G6","object":"chat.completion.chunk","created":6,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Par"},"finish_reason":null}]}

data: {"error":{"message":"Rate limit reached","type":"rate_limit_error","code":"rate_limit_exceeded"}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_08HiJkLmNoPqRsTuVwXyZaBc","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Par"}}

event: error
data: {"type":"error","error":{"type":"rate_limit_error","message":"Rate limit reached"}}

```

## Example 6c: context-length overflow reported in-band before anything was written

Input:

```text
data: {"error":{"code":"context_length_exceeded","message":"This model's maximum context length is 8192 tokens."}}

```

Output (the error frame is the only frame; the message text starts with `prompt is too long`, exact wording per `04-harness-and-errors.md`):

```text
event: error
data: {"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: This model's maximum context length is 8192 tokens."}}

```

---

## Example 7a: connection reset after some text

Input: the first two chunks of Example 6a (role chunk, then `"Par"`), then the connection is reset.

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_09IjKlMnOpQrStUvWxYzAbCd","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Par"}}

event: error
data: {"type":"error","error":{"type":"api_error","message":"Upstream connection lost"}}

```

## Example 7b: clean EOF with neither finish_reason nor `[DONE]`

Input: the first two chunks of Example 6a, then the upstream closes the connection cleanly.

Output: identical to 7a except the final frame:

```text
event: error
data: {"type":"error","error":{"type":"api_error","message":"Upstream stream ended before completion"}}

```

## Example 7c: EOF after the finish_reason, no usage, no `[DONE]` (clean end, estimated usage)

Input:

```text
data: {"id":"chatcmpl-H7","object":"chat.completion.chunk","created":7,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-H7","object":"chat.completion.chunk","created":7,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Hello there"},"finish_reason":null}]}

data: {"id":"chatcmpl-H7","object":"chat.completion.chunk","created":7,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

```

then the upstream closes the connection. Assumption for this example: the estimator from 04 yields 3 for the 11-character output.

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_10JkLmNoPqRsTuVwXyZaBcDe","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello there"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":3}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: the finish was seen, so EOF is a clean end. If the upstream had stayed connected and silent instead, the same frames would be written 2 seconds (FINISH_GRACE) after the finish chunk.

---

## Example 8: whole tool calls without `index` in separate chunks, finish_reason `stop`

Input:

```text
data: {"id":"g-1","object":"chat.completion.chunk","created":8,"model":"gem-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"g-1","object":"chat.completion.chunk","created":8,"model":"gem-x","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"p\":1}"}}]},"finish_reason":null}]}

data: {"id":"g-1","object":"chat.completion.chunk","created":8,"model":"gem-x","choices":[{"index":0,"delta":{"tool_calls":[{"id":"call_2","type":"function","function":{"name":"read_file","arguments":"{\"p\":2}"}}]},"finish_reason":null}]}

data: {"id":"g-1","object":"chat.completion.chunk","created":8,"model":"gem-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":30,"completion_tokens":12,"total_tokens":42}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_11KlMnOpQrStUvWxYzAbCdEf","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_1","name":"read_file","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"p\":1}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"call_2","name":"read_file","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"p\":2}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":30,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":12}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: with no `index`, the second entry carries a different id, so it starts a new call; the first call's held arguments are a complete object, so it is written when the second call starts. The finish_reason `stop` is overridden by `tool_use` because tool_use blocks were emitted.

---

## Example 9: text after a tool call

Input:

```text
data: {"id":"chatcmpl-I9","object":"chat.completion.chunk","created":9,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

data: {"id":"chatcmpl-I9","object":"chat.completion.chunk","created":9,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"p\":1}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-I9","object":"chat.completion.chunk","created":9,"model":"gpt-x","choices":[{"index":0,"delta":{"content":"Done."},"finish_reason":null}]}

data: {"id":"chatcmpl-I9","object":"chat.completion.chunk","created":9,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":30,"completion_tokens":12,"total_tokens":42}}

data: [DONE]

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_12LmNoPqRsTuVwXyZaBcDeFg","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_1","name":"read_file","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"p\":1}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Done."}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":30,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":12}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: the text piece completes the pending call (written first), then opens a new text block. A later fragment for the completed call would be discarded.

---

## Example 10: interleaved argument fragments, calls without ids, reverse tool-name map

Request context: the request mapping sanitized the client's tool `mcp__srv__do.it` to `mcp__srv__do_it`; the reverse map restores it. The upstream sends no ids.

Input:

```text
data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"role":"assistant","content":null},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"type":"function","function":{"name":"mcp__srv__do_it","arguments":""}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"type":"function","function":{"name":"plain_tool","arguments":""}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"a\":"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{ \"b\" : "}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"1}"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"2.50 }"}}]},"finish_reason":null}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

data: {"id":"chatcmpl-J10","object":"chat.completion.chunk","created":10,"model":"gpt-x","choices":[],"usage":{"prompt_tokens":80,"completion_tokens":20,"total_tokens":100}}

```

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_13MnOpQrStUvWxYzAbCdEfGh","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_9aXk2LmQ7rTz4VbN1cYd0HsP","name":"mcp__srv__do.it","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"a\":1}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: content_block_start
data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_3Fw8KpZ5nJ6eRq2UmB7tYc1D","name":"plain_tool","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"b\":2.50}"}}

event: content_block_stop
data: {"type":"content_block_stop","index":1}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"input_tokens":80,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":20}}

event: message_stop
data: {"type":"message_stop"}

```

Notes: when call 1 starts, call 0 still has empty arguments, so the stream enters hold mode: no call completes by index change. Both calls are emitted at the finish, in ascending index order, one after the other. Missing ids are synthesized (`toolu_` + 24 characters). The reverse map restores the original tool name. Whitespace inside the arguments is removed while the number lexeme `2.50` is kept.

---

## Example 11: upstream silent for 20 seconds before the first chunk

Timeline: upstream 2xx at t=0; first chunk at t=20 s; no frame has been written before t=8 s.

- t=8 s: no upstream chunk yet, 8 seconds idle: message_start and ping are written.
- t=16 s: still no chunk, 8 seconds since the last write: ping.
- t=20 s: first chunk arrives and is processed normally; the message is already started.

Output:

```text
event: message_start
data: {"type":"message_start","message":{"id":"msg_14NoPqRsTuVwXyZaBcDeFgHi","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1234,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}

event: ping
data: {"type":"ping"}

event: ping
data: {"type":"ping"}

event: content_block_start
data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

event: content_block_delta
data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Ok"}}

event: content_block_stop
data: {"type":"content_block_stop","index":0}

event: message_delta
data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":20,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":2}}

event: message_stop
data: {"type":"message_stop"}

```

Input chunks at t=20 s (role chunk, content `"Ok"`, finish `stop` with usage prompt 20 / completion 2) are omitted for brevity; the same silent-period pings (every 8 s since the last frame) would also be written while a tool call's arguments are held.
