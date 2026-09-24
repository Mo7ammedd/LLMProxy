# Provider operations and alerts

Version 0.3 adds **Providers** and **Alerts** to `/admin`. All management reads require an administrator, operator or auditor. Provider mutations and access checks require an administrator; operators can also acknowledge alerts. Gateway credentials do not authorize management requests.

## Encrypted provider keys

Generate a persistent encryption key once with `openssl rand -base64 32` and install it as `LLMPROXY_PROVIDER_KEY_ENCRYPTION_KEY` on every replica. A secret manager or protected deployment environment is recommended. The equivalent nested setting is `LLMProxy:Operations:EncryptionKey`; the flat variable takes precedence. The value must decode to exactly 32 bytes. Empty configuration disables adding managed credentials without affecting existing environment/JSON keys or their enable/disable controls.

The **Providers** tab lists all built-in and configured named accounts, their availability, and each key's fingerprint, label, source, enabled state, cooldown, attempts, failures, tokens and estimated/reconciled cost over the last 24 hours. Statistics come from retained, completed accounting records. HTTP failure counts include missing responses and status codes of 400 or higher; partial stream failures after successful HTTP establishment can still have HTTP 200. Live checks do not enter billing records.

An administrator can add keys and enable or disable existing keys. Added secrets are encrypted using AES-256-GCM with a random 96-bit nonce and a 128-bit authentication tag; account name and credential fingerprint are authenticated as associated data. API responses, audit records and usage reports never contain raw keys or ciphertext. Key fingerprints remain sensitive operational metadata and are restricted to management users.

Environment/JSON keys are the base configuration. A nonempty `ApiKeys` array still replaces that account's single `ApiKey`. Managed keys append to this base; enable/disable overrides apply by fingerprint to either source. Duplicate additions return `provider_key_exists` (409). The union supports at most 64 keys, including disabled credentials. Labels are limited to 128 characters; secrets to 8192 printable ASCII characters without whitespace. Changing provider endpoints, adapters, model mappings and prices still uses configuration and `/admin/config/reload`. Entra ID accounts acquire their credentials through Azure.Identity and do not accept managed API keys.

The encryption key must be backed up **separately** from the database. Losing it makes stored credentials unrecoverable. Do not replace it on a running deployment: v0.3 has no automatic master-key rotation/rewrap command. Restoring encrypted credentials requires the matching encryption key. Readiness includes managed-key refresh without contacting upstream providers. Wrong keys or tampered ciphertext fail with `key_decryption_failed` (503), without exposing the underlying exception. Disabled encrypted credentials are retained and need the original encryption key before they can be re-enabled.

| Method and path | Body / result |
| --- | --- |
| `GET /admin/providers` | Account summaries and safe key metadata; no secrets |
| `POST /admin/providers/{provider}/keys` | `{"key":"NEW_PROVIDER_CREDENTIAL","label":"secondary"}` → 204 |
| `PUT /admin/providers/{provider}/keys/{keyId}` | `{"enabled":false,"label":"secondary"}` → 204 |
| `POST /admin/providers/{provider}/check` | `{}` for model discovery, or `{"model":"configured-upstream-model"}` for a generation check |

`{provider}` is the actual account ID, such as `openai` or `openai-east`. `{keyId}` is the `key_` fingerprint returned by the list endpoint. Put credentials in a protected JSON body file when using curl and use `--data-binary @file.json`; do not put them into command arguments or shell history.

## Replicas and cooldowns

Managed key writes and their audit event commit atomically with a monotonically increasing database revision. Each authenticated `/v1` HTTP request checks this revision before routing. Background workers refresh at most every five seconds while the database is reachable. An in-flight request or stream keeps its captured credentials; disabling a key affects newly routed requests. All replicas must share the database, encryption key, Redis namespace and base account definitions. Changing environment/JSON configuration still requires reload on each replica or a restart.

PostgreSQL mode uses Redis for atomic rotation and maximum cooldown deadlines. Cooldowns use Redis server time and survive catalog reloads. Keys include the account name, so two accounts using the same adapter/secret do not affect each other. Redis outages return `provider_state_unavailable` (503), rather than bypassing shared controls. Standalone mode uses process memory; rotation/cooldowns reset on restart. Redis state expires after two days without pool activity; configured cooldowns remain bounded by the upstream retry delay (at most one day).

Only pools with multiple enabled keys use cooldown/failover behavior. Their 401/403/429 responses cool the selected key; pooled 429 switches keys without retrying the same key. Once HTTP succeeds, the response/stream stays on that credential. When every key is cooling, requests return `provider_keys_unavailable` with `Retry-After`. Disabling all keys makes a keyed account unavailable to routing, allowing configured provider fallback.

## Optional live checks

Set `LLMProxy__Operations__LiveChecksEnabled=true` to expose live checks. They remain disabled by default and never run from readiness/liveness probes, application startup, ordinary CI or alert evaluation. Requests are limited to two checks per minute per account. Each enabled key gets one attempt with a ten-second timeout, without changing production cooldowns or replaying a successful response. Caller cancellation stops the check.

An empty body object requests the provider's model-list endpoint. A successful model list establishes access to that endpoint; it does not prove generation access to every listed model. Lists are bounded to 1 MiB and 1000 names. Azure and Foundry Entra ID accounts require a model generation check. The model must match an upstream model already mapped to that provider account in the runtime catalog.

A generation check sends `Reply OK.` with a 16-token output limit through the configured adapter. It can incur provider charges; these diagnostic calls are outside gateway-key quotas and usage accounting. The returned records contain key ID, success, safe result code, status, latency and model names. A valid key may still fail a particular model or operation. No production-provider credentials are used by automated tests.

## Alert configuration

Alert evaluation is enabled by default; webhook delivery stays off until a URL is configured. Process settings require restart. Compose forwards the following variables from `.env`; source/Docker deployments can use the same environment names or corresponding colon-separated JSON paths.

| `LLMProxy:Alerts` setting | Default | Bounds / meaning |
| --- | --- | --- |
| `Enabled` | `true` | Enable automatic evaluation and configured delivery |
| `EvaluationSeconds` | `60` | 10–3600 seconds |
| `WindowMinutes` | `5` | 1–1440; recent upstream-attempt window |
| `MinimumAttempts` | `20` | 1–1,000,000; minimum for error/latency alerts |
| `BudgetPercent` | `80` | Greater than 0, at most 100; lifetime and current UTC-month spending plus reservations |
| `ErrorPercent` | `20` | Greater than 0, at most 100; failed upstream HTTP attempts |
| `AverageLatencyMs` | `10000` | 1–3,600,000; average upstream HTTP-attempt latency |
| `WebhookUrl` | empty | Optional HTTPS receiver URL |
| `WebhookBearerToken` | empty | Optional Authorization bearer credential |
| `AllowInsecureWebhook` | `false` | Explicitly allow HTTP for a trusted local receiver |

Budget alerts cover enabled, unexpired gateway keys with a positive dollar budget; they include reservations and invoice adjustments. Existing token limits remain enforced but do not produce budget alerts. Error and latency alerts aggregate HTTP attempts, including retries, by provider account. Latency is time to HTTP response headers, not full stream duration. Pool alerts fire when every key is disabled or all keys in a multi-key pool are cooling down. Detection has the configured evaluation interval; short cooldowns may end between evaluations.

Incidents persist in SQLite/PostgreSQL and deduplicate across replicas. They resolve when the condition clears. Recurrence reopens the incident, increments `occurrences`, and resets acknowledgment/delivery. Resolved history is retained for 90 days; active incidents are retained. Acknowledgment records the operator and suppresses pending webhook delivery for that occurrence; it does not change the underlying condition.

| Method and path | Purpose |
| --- | --- |
| `GET /admin/alerts?resolved=true&limit=100` | Latest incidents, optionally including resolved; limit 1–1000 |
| `GET /admin/alerts/settings` | Thresholds and whether delivery is configured; omits URL and token |
| `POST /admin/alerts/evaluate` | Administrator-triggered evaluation; no immediate external delivery |
| `POST /admin/alerts/{id}/acknowledge` | Administrator/operator acknowledgment → 204 |

Webhook POSTs contain `id`, `occurrence`, `kind`, `resource`, `severity`, `message`, and `started_at`. Use a receiver that accepts this JSON format; Slack/email integrations need a receiver that translates it. Delivery claims use a one-minute database lease, a five-second network timeout, no redirects or HTTP retries, and a five-minute delay after failure. A worker sends at most ten notifications per evaluation. Delivery is at least once: after a crash, the receiver must deduplicate using the `Idempotency-Key` header (`incident-id:occurrence`). The gateway never includes prompts, provider secrets, webhook credentials or upstream response bodies in the payload. Webhook URLs are suppressed from HTTP telemetry. Resolution is visible in the API/dashboard; webhook notifications are sent on activation, not resolution.
