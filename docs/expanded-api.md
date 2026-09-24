# Management and extended APIs

Version 0.2 adds capability routing, named accounts, monthly allowances, operator identities, reporting and additional inference protocols. The [OpenAPI document](openapi.yaml) describes the HTTP shapes; [configuration](configuration.md) describes deployment settings.

## Operator access and dashboard

Open `/admin` for the embedded gateway console. It includes usage totals and charts, filters and CSV export, key creation/editing/rotation, operator creation and enable/disable controls, audit history and model configuration. Assets are served locally under a restrictive content security policy. Bearer credentials stay in browser memory; refreshing the page requires signing in again.

Set `LLMPROXY_ADMIN_KEY` to bootstrap the first local administrator through `POST /admin/operators`. The shared key has administrator privileges. After creating and verifying a local administrator, the shared key can be removed on restart; existing local accounts continue to work. Gateway keys cannot authenticate to management endpoints.

| Role | Permissions |
| --- | --- |
| `auditor` | Read keys, usage, audit and models; export reports |
| `operator` | Auditor permissions plus key creation, policy updates and rotation |
| `administrator` | Operator permissions plus operator management, invoice reconciliation and configuration reload |

| Method and path | Behavior |
| --- | --- |
| `POST /admin/auth/login` | Accepts `username` and `password`; returns `token`, `expires_at` and `operator` |
| `GET /admin/auth/me` | Current `id`, `username` and `role` |
| `POST /admin/auth/logout` | Revokes the presented local session; returns 204 |
| `GET /admin/operators` | Lists local operator metadata; administrator only |
| `POST /admin/operators` | Creates `{username,password,role}`; administrator only |
| `PUT /admin/operators/{id}` | Replaces `{enabled,role,password?}`; administrator only |

Usernames are normalized to lowercase. Passwords require at least 12 characters and are hashed with PBKDF2-SHA256 and a per-account salt. Local bearer sessions expire after eight hours; password or role updates revoke sessions. Login is limited by both username and source IP. The last enabled local administrator cannot be disabled or demoted. This is local role-based access; OIDC/SSO and MFA are not implemented. Signing out does not revoke a shared bootstrap secret.

Management mutations have an intent audit row and an outcome row. Rejected management and `/v1` requests also receive audit rows. Audit fields are actor, HTTP action, route template, status, request ID and UTC time. They exclude credentials, passwords, request bodies and prompts. If the initial management audit cannot be persisted, the mutation fails with 503; later audit write failures are logged.

## Key lifecycle and monthly allowances

`POST /admin/keys` and `PUT /admin/keys/{id}` accept the existing lifetime policy plus:

```json
{
  "expires_at": "2027-01-01T00:00:00Z",
  "monthly_token_limit": 1000000,
  "monthly_spending_budget": 20
}
```

Null means no expiry or no limit. Limits can be zero. A policy update replaces the full policy, so include every limit that should remain configured. Expired or disabled keys fail authentication. Admission rechecks their enabled/expiry state and current allowances in the database.

Monthly windows follow UTC calendar months, with independent token and spending counters. Admission atomically reserves both lifetime and monthly allowances; completion settles both. A request started before midnight on the last day of a month settles against that original month even if it finishes in the next one. Unused allowance does not carry over. Policy changes do not erase consumption.

`POST /admin/keys/{id}/rotate` takes `{"grace_seconds":3600}` and returns a new one-time `key` and `details`. Grace defaults to zero and is limited to 86,400 seconds. The key ID, owner, policies, usage and file/batch ownership remain unchanged. Only the current and immediately previous credentials can remain valid; another rotation retires older credentials. Both credentials obey the key's current expiry and enabled state.

`GET /admin/keys/{id}/quotas` returns up to 120 monthly windows, newest first. `period` uses `yyyy-MM`. `spent_units` and `reserved_units` are integer nano-USD: 1,000,000,000 units equal USD 1. Key summaries expose dollar amounts as `spent` and `reserved_cost`.

CLI creation accepts `--expires-at`, `--monthly-token-limit` and `--monthly-budget` alongside the lifetime flags:

```bash
dotnet run --project src/LLMProxy.Server -- keys create \
  --owner search-api --models fast,embeddings --rpm 60 \
  --monthly-token-limit 1000000 --monthly-budget 20 \
  --expires-at 2027-01-01T00:00:00Z
```

## Reports and reconciliation

| Method and path | Behavior |
| --- | --- |
| `GET /admin/keys/page` | Key metadata filtered by exact `owner` |
| `GET /admin/usage/page` | Cursor-paginated usage |
| `GET /admin/usage/summary` | Database rollups by model/provider: requests, input/output tokens, cost and mean latency |
| `GET /admin/usage/export` | Streamed CSV for matching usage, across all pages |
| `GET /admin/usage/{id}/attempts` | Individual upstream HTTP attempts, including retries and fallback |
| `GET /admin/audit` | Cursor-paginated audit records |
| `POST /admin/billing/reconcile` | Atomic invoice adjustment import; administrator only |

Page endpoints accept `limit` (1–1,000, default 100) and opaque `cursor`, returning `{"data":[...],"next_cursor":"..."}`. The final page omits `next_cursor` or returns null. URL-encode the cursor when sending it back. Ordering uses creation time and ID so equal timestamps do not skip rows. Keep filters unchanged while traversing pages. The legacy `/admin/keys` and `/admin/usage` array responses remain available.

Usage page, summary and export filters are `api_key_id`, exact `owner`, `from` (inclusive), `to` (exclusive), `model`, `provider` and `status`. Dates use ISO 8601 with an offset, preferably UTC. Records include `operation` (`chat`, `embeddings`, `responses`), `usage_estimated`, status/error and cost. Rollups cover all matching retained rows. Historical reports reflect retention, while key counters retain all consumption.

Prices support cached input, cache creation and input-size tiers. HTTP attempts retain provider request IDs when supplied, HTTP status, timing, token/cache details and estimated cost. `provider_key_id` identifies the selected upstream credential as `key_` plus the first 32 lowercase hexadecimal characters of its SHA-256 digest. It is stable across reloads and contains no raw key material; it is null for historical attempts, identity/keyless requests or attempts that never reached HTTP. [Key pools](configuration.md#multiple-api-keys) retain separate attempt rows under the same provider name, and gateway quota is settled once per request. Failed attempts without upstream usage remain estimated and may later require an invoice adjustment. Request totals cannot guarantee exact provider billing, especially after retries or a process crash.

Reconcile up to 1,000 records at a time:

```json
[
  {
    "attempt_id": "45f428a7-cb2d-4bda-a239-63945ab25eb5",
    "actual_cost": 0.0125,
    "reference": "invoice-2026-09/line-42"
  }
]
```

The import adjusts request cost and lifetime/original-month spending by the difference from the attempt estimate. It does not rewrite token usage. Repeating an identical record is idempotent; reusing an attempt or reference with different values returns 409. New adjustments require retained usage and attempts. References should identify individual invoice lines. There is no automatic invoice download or provider-specific invoice parser.

## Embeddings

`POST /v1/embeddings` accepts `model`, `input`, optional `dimensions`, `encoding_format` (`float` or `base64`) and `user`. Input can be a string, a string array, an integer token array or an array of token arrays. Limits are 2,048 inputs and 8,192 tokens per explicit token array; configured request/context limits also apply. Upstream models determine valid dimensions and token vocabularies.

```json
{"model":"embeddings","input":["First document","Second document"],"encoding_format":"float"}
```

Adapters: OpenAI, Azure OpenAI, Foundry, Mistral and Ollama. The default `embeddings` alias maps OpenAI `text-embedding-3-small`, Mistral `mistral-embed` and Ollama `nomic-embed-text`. It disables fallback because embeddings from different models are generally incompatible. Configure a fixed alias/account for a persistent vector index. Install the Ollama embedding model before selecting it.

The response is an OpenAI embedding list with the public alias and input-only usage. The normal key permissions, RPM limits, concurrency and quota accounting apply.

## Responses

`POST /v1/responses` supports normal JSON and native Responses SSE events through OpenAI and Foundry. It forwards native output items, rewrites the top-level response model to the public alias, and records usage through normal gateway accounting. It does not convert Responses requests to Chat Completions.

```json
{"model":"fast","input":"Explain a database transaction.","max_output_tokens":256,"store":false}
```

Supported controls include explicit conversation input, instructions, function tools, tool selection, structured text output, reasoning effort, temperature/top-p where the model supports them, and text/image input. Function outputs support strings or validated text/image parts. Streaming uses events such as `response.output_text.delta` and `response.completed`; failures after output use a sanitized native `error` event. There is no Chat Completions `[DONE]` marker. Fallback stops after the first event, including a lifecycle event.

Responses are **stateless**: `store:false` is enforced and inserted when omitted. Send the conversation items with each request. Stored/retrieved responses, `previous_response_id`, conversations, background execution, hosted tools, item references and provider file references are rejected. Gateway batch files are not upstream provider files. This avoids sharing unowned provider state across gateway keys. Native IDs in returned response items can be included as part of explicit conversation history where the provider accepts them.

Python and TypeScript examples are in [protocols.py](../examples/python/protocols.py) and [protocols.ts](../examples/typescript/protocols.ts). The Python example also uploads and runs a batch.

## Chat media and reasoning controls

User message content arrays support `text`, `image_url` and `input_audio` parts. Image URLs must use HTTPS or base64 data URLs with PNG/JPEG/WebP/GIF MIME types. Audio uses base64 `data` with `format` set to `wav` or `mp3`. The gateway forwards or translates the media to a compatible adapter; model capability declarations must also allow it.

```json
{
  "model": "fast",
  "messages": [{"role":"user","content":[
    {"type":"text","text":"Describe this image."},
    {"type":"image_url","image_url":{"url":"https://example.com/image.png"}}
  ]}]
}
```

`reasoning_effort` accepts `none`, `minimal`, `low`, `medium`, `high` and `xhigh` on compatible OpenAI/Azure/Foundry/Groq models. Each upstream model supports a subset. `thinking_budget_tokens` maps to Anthropic/Gemini and must be at least 1,024, below the total output limit, and used without `reasoning_effort`. Anthropic thinking plus function tools is excluded because this chat adapter does not retain thinking signatures. DeepSeek continues to preserve its reasoning history while leaving thinking defaults to the model.

Remote-media token counts and model-specific reasoning restrictions cannot be inferred exactly. Conservative input estimates are based on serialized bytes, not a media tokenizer. Provider usage settles the actual reported counts; configure budgets and context limits accordingly. Image generation, speech generation/transcription, video and log probabilities are not exposed.

## Files and durable batches

Files and batches use the gateway key bearer credential and are isolated by key ID.

| Method and path | Behavior |
| --- | --- |
| `POST /v1/files` | Multipart upload with one `file` and `purpose=batch` |
| `GET /v1/files` | List owned files with `after` and `limit` (1–100, default 20) |
| `GET /v1/files/{id}` | File metadata |
| `GET /v1/files/{id}/content` | Download UTF-8 JSONL |
| `DELETE /v1/files/{id}` | Delete a file; active batch inputs cannot be deleted |
| `POST /v1/batches` | Create a batch with `input_file_id`, `endpoint`, `completion_window:"24h"` and optional string metadata |
| `GET /v1/batches` | List owned batches with `after` and `limit` |
| `GET /v1/batches/{id}` | Status, counts and output/error file IDs |
| `POST /v1/batches/{id}/cancel` | Stop pending work; running items finish before cancellation completes |

Each input line has a unique `custom_id`, `method:"POST"`, a `url` matching the batch endpoint, and a nonstreaming `body`:

```json
{"custom_id":"row-1","method":"POST","url":"/v1/chat/completions","body":{"model":"fast","messages":[{"role":"user","content":"Hello"}]}}
```

Supported endpoints are chat completions, embeddings and Responses. Defaults are a 10 MiB file, 1,000 requests and four workers per instance. Workers claim durable items through the database across replicas. A lost worker's running item becomes `batch_item_interrupted`; it is never automatically replayed because upstream usage may already have occurred. Running items can finish after the 24-hour admission window. Clients can retrieve partial results from cancelled or expired batches.

These are gateway-managed batches. They use ordinary provider requests and per-item permissions, RPM, concurrency, output limits and quota accounting. Admission failures become item errors; there is no special native provider batch discount or automatic item retry queue. Set `LLMProxy:Batches:Workers=0` on API-only instances and run workers on at least one other replica.

Batch input and output bodies are intentionally persisted in the database and backups. They are available only through their owning gateway key. Finished jobs/results and unreferenced uploaded files are retained for seven days by default. Usage defaults to 90 days and audit to 365 days; see [retention settings](configuration.md#storage). Download needed results before expiry.

Protocol references: [OpenAI Embeddings](https://developers.openai.com/api/reference/resources/embeddings/methods/create), [Responses](https://developers.openai.com/api/reference/resources/responses/methods/create) and [Batches](https://developers.openai.com/api/reference/resources/batches/methods/create). The gateway supports the subsets and operational limits described above.
