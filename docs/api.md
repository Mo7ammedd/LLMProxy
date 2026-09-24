# API reference

Base URL: `http://localhost:4000/v1`. All `/v1` operations require `Authorization: Bearer <gateway-key>`. Keys normally begin with `llmp_sk_`; provider credentials are never accepted as gateway credentials.

Every response includes a generated `X-Request-Id`. Clients may send a separate `X-Correlation-Id` containing at most 64 ASCII letters, digits, `.`, `_` or `-`. Invalid correlation IDs are replaced. Client-supplied IDs cannot overwrite database request IDs.

## List models

`GET /v1/models`

```json
{
  "object": "list",
  "data": [{ "id": "fast", "object": "model", "created": 0, "owned_by": "llmproxy" }]
}
```

Only aliases both permitted by the key and backed by a configured provider are returned. Model listing consumes the key and owner RPM limits, but not model RPM or tokens.

## Chat completion

`POST /v1/chat/completions` with `Content-Type: application/json`:

```json
{
  "model": "fast",
  "messages": [
    { "role": "system", "content": "Answer briefly." },
    { "role": "user", "content": "Hello" }
  ],
  "max_tokens": 128
}
```

The response uses the OpenAI chat-completion envelope:

```json
{
  "id": "chatcmpl-<gateway-request-id>",
  "object": "chat.completion",
  "created": 1770000000,
  "model": "fast",
  "choices": [{
    "index": 0,
    "message": { "role": "assistant", "content": "Hello!" },
    "finish_reason": "stop"
  }],
  "usage": { "prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5 }
}
```

The returned model is always the requested public alias. Underlying provider IDs and credentials do not need to be known by the application. Upstream token reports are used where available. Anthropic cached input, Gemini thought tokens and DeepSeek completion/reasoning totals are included in normalized usage. Cohere prefers actual token counts over billed units; Groq's final `x_groq.usage` is normalized to the same usage envelope.

## Streaming

Set `"stream": true`. Optional `"stream_options": {"include_usage": true}` requests a final usage event. The content type is `text/event-stream`, caching is disabled and `X-Accel-Buffering: no` is returned.

```text
data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":1770000000,"model":"fast","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"},"finish_reason":null}],"usage":null}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":1770000000,"model":"fast","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":null}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","created":1770000000,"model":"fast","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

data: [DONE]
```

Without `include_usage`, usage fields/events are omitted from the downstream stream, while accounting still captures upstream usage. If an error occurs before output, the HTTP status and error envelope describe the failure. If output has already begun, the HTTP status remains 200 and a final `data: {"error": ...}` event describes the error; there is no success `[DONE]` marker. Clients should treat such streams as incomplete. Disconnects cancel upstream work.

## Compatibility

The supported surface is Chat Completions and model listing, not the entire OpenAI product API. The [official Chat Completions reference](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) defines the envelope and SSE usage convention used here.

| Feature | OpenAI / Azure / Foundry | Anthropic | Gemini |
| --- | --- | --- | --- |
| Text messages, text-part arrays | Yes | Yes | Yes |
| System/developer instructions | Yes | Combined as system instructions | Combined as system instructions |
| Normal and streamed text | Yes | Yes | Yes |
| Function tools and tool results | Yes | Native tool-use translation | Native function-call translation |
| Tool choice: auto/none/required/named | Yes | Yes | Yes |
| `parallel_tool_calls: false` | Yes | Yes | Rejected |
| `temperature` | 0–2; model restrictions may apply | 0–1 | 0–2; model restrictions may apply |
| `top_p`, `stop` | Yes | Yes | Yes |
| `max_tokens`, `max_completion_tokens` | Normalized to `max_completion_tokens` | Translated to `max_tokens` | Translated to `maxOutputTokens` |
| `response_format` | Forwarded | Rejected | JSON object/schema translation |
| `seed`, message `name`, strict tools | Forwarded | Rejected | Rejected |
| `user` metadata | Forwarded | Omitted | Omitted |

The added adapters also support text, text-part arrays, tools/results and normal/SSE responses, with these differences:

| Provider | Request translation and limits |
| --- | --- |
| Mistral | Developer → system; `max_tokens`; `seed` → `random_seed`; native streamed usage without `stream_options`. Tool arguments returned as objects are converted to JSON strings. Historical tool IDs are mapped consistently to nine alphanumeric characters upstream. Message `name` is accepted only for tool results. |
| Cohere | Native v2 Chat/SSE translation; developer → system; `max_tokens`; `top_p` → `p` (0.01–0.99); `stop` → `stop_sequences`; seed supported. Named tool choice filters the offered functions and uses `REQUIRED`. Strict tools require all selected functions to be strict. JSON object/schema output cannot be combined with tools. `parallel_tool_calls: false` and message names are rejected. |
| DeepSeek | Developer → system; `max_tokens`; JSON object output; reasoning history preserved. Seed, JSON schema output, strict beta tools and `parallel_tool_calls: false` are rejected. Thinking defaults are left to the model; forced tool choices may be rejected by a thinking model. |
| Groq | Developer → system; `max_completion_tokens`; seed, tools and supported structured-output formats forwarded. Message names are rejected. Native final-chunk usage is collected without sending `stream_options`. |
| Ollama | Developer → system; `max_tokens`; seed and response formats forwarded. Auto tool choice is implicit; `none` removes offered tools. Required/named choices, strict tools, message names and `parallel_tool_calls: false` are rejected. Provider authentication is optional. |

`user` metadata is omitted for Mistral, Cohere, DeepSeek and Ollama. Foundry supports OpenAI v1 chat deployments; model capabilities depend on the deployment and do not include Foundry Agents or the separate Anthropic API.

Provider/model availability and restrictions still apply. Known unsupported adapter parameters fail with `unsupported_parameter`; restrictions enforced upstream return a sanitized `provider_rejected_request`. Capability-based routing is not implemented. Use a dedicated alias with compatible providers for provider-specific features. See the [provider guide](providers.md) for setup and model defaults.

MVP boundaries:

- Exactly one completion choice (`n=1`). Images, audio, video, embeddings, Responses API, batches, stored completions and log probabilities are not implemented.
- Unknown top-level chat parameters are rejected. Input text is required unless an assistant message contains tool calls.
- System/developer messages must precede the conversation. Every tool response must match an outstanding assistant tool call; outstanding calls must be resolved before the next non-tool message.
- Function names use ASCII letters, numbers, `_` and `-`, up to 64 characters. Tool arguments must be valid JSON objects. The gateway never executes a tool.
- Gemini thought signatures attached to function calls are preserved in `tool_calls[].extra_content.google.thought_signature`. Applications using such models must retain this opaque metadata in subsequent tool history. SDKs that discard unknown fields may require explicit metadata preservation or a model without that requirement.
- DeepSeek's optional assistant `reasoning_content` and streamed `delta.reasoning_content` are preserved separately from `content`. Retain this field in tool history for thinking models; SDKs that discard unknown fields need explicit metadata preservation. It is stripped when forwarding history to other adapters. It is never logged. Gemini/Mistral thought blocks and Cohere tool plans are not exposed. Provider-specific thinking/effort controls are not accepted in this version.
- Request and output limits are configurable. The default inbound body limit is 1 MiB and default output cap is 1,024 tokens.

## Errors

Errors under `/v1/*` use:

```json
{
  "error": {
    "message": "A valid gateway API key is required.",
    "type": "authentication_error",
    "param": null,
    "code": "invalid_api_key"
  }
}
```

| Status | Typical codes | Meaning |
| --- | --- | --- |
| 400 | `invalid_json`, `invalid_request`, `unsupported_parameter`, `provider_rejected_request` | Invalid/unsupported input |
| 401 | `invalid_api_key` | Missing, unknown or disabled gateway key |
| 403 | `model_not_allowed` | Key has no access to that alias |
| 404 | `model_not_found`, `not_found` | Unknown alias or endpoint |
| 413 | `invalid_request` | Request body exceeds the configured limit |
| 415 | `invalid_content_type` | A JSON body is required |
| 429 | `rate_limit_exceeded`, `insufficient_quota`, `provider_rate_limited` | RPM, token/budget or upstream allowance exhausted |
| 502 | `provider_unavailable`, `provider_connection_error`, `invalid_provider_response` | Upstream failure |
| 503 | `provider_unavailable`, `rate_limit_unavailable`, `routing_unavailable`, `storage_unavailable` | Gateway dependency unavailable |
| 504 | `request_timeout`, `provider_timeout` | Request/provider deadline exceeded |

RPM denials include `Retry-After` in seconds. Lifetime quota denials do not have a time-based reset. Provider error bodies, internal exception messages, stack traces and credentials are never returned. Management endpoints use RFC 7807 problem details with a safe `code` and `request_id`.

## Administration

The management API is disabled unless `LLMPROXY_ADMIN_KEY` is set. Use that secret as the bearer credential. It is separate from gateway API keys and should only be reachable by operators.

Create a key:

```bash
curl http://localhost:4000/admin/keys \
  -H "Authorization: Bearer $LLMPROXY_ADMIN_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"owner":"payments-service","allowed_models":["fast"],"requests_per_minute":60,"token_limit":1000000,"spending_budget":20}'
```

The 201 response contains `key` once and `details` containing its ID and policy. Lists and updates never return the raw key or hash.

`GET /admin/keys?limit=100` lists key metadata; `limit` is clamped to 1–1,000. `PUT /admin/keys/{id}` replaces the policy:

```json
{
  "enabled": false,
  "allowed_models": ["fast"],
  "requests_per_minute": 60,
  "token_limit": 1000000,
  "spending_budget": 20
}
```

Supply the full policy when updating. Omitted nullable limits become unlimited; consumed counters and ownership are preserved. `allowed_models: ["*"]` grants all configured aliases. Lowering a limit below current consumption prevents subsequent admissions.

`GET /admin/usage?api_key_id=<uuid>&limit=100` lists the newest admitted request records, up to 1,000 per request. Records include `request_id`, `api_key_id`, `model`, `provider`, input/output/total tokens, latency, status, nullable error code, estimated cost, `usage_estimated` and UTC creation time. It is an operational listing endpoint; arbitrary analytics and pagination exports are not part of this MVP.
