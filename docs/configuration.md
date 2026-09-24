# Configuration reference

The server reads `appsettings.json`, environment-specific .NET configuration and environment variables. Settings are strongly typed and validated at startup. Models, capabilities, prices and provider accounts support explicit validated reload; other settings require restart. Never put credentials in committed JSON. Environment names are case-sensitive on Linux; .NET configuration keys are case-insensitive.

## Storage

| Setting | Default | Meaning |
| --- | --- | --- |
| `LLMProxy:Storage:Mode` | `Standalone` | `Standalone` uses SQLite; `PostgreSql` requires PostgreSQL and Redis |
| `LLMProxy:Storage:SqlitePath` | `data/llmproxy.db` | Relative to the server working directory; image sets `/data/llmproxy.db` |
| `LLMProxy:Storage:AutoMigrate` | `true` | Apply database migrations during startup |
| `LLMProxy:Storage:RedisKeyPrefix` | `llmproxy` | Shared rate/cursor/concurrency namespace across replicas; braces are prohibited |
| `LLMProxy:Storage:UsageRetentionDays` | `90` | Usage and attempt retention; zero disables deletion |
| `LLMProxy:Storage:AuditRetentionDays` | `365` | Audit retention; zero disables deletion |
| `LLMProxy:Storage:BatchRetentionDays` | `7` | Finished batches/results and unreferenced input files; 1–365 days |
| `ConnectionStrings:Postgres` | unset | Npgsql connection string |
| `ConnectionStrings:Redis` | unset | StackExchange.Redis connection string |

Environment example:

```dotenv
LLMProxy__Storage__Mode=PostgreSql
ConnectionStrings__Postgres=Host=postgres;Database=llmproxy;Username=llmproxy;Password=REPLACE
ConnectionStrings__Redis=redis:6379,password=REPLACE,abortConnect=false
```

For remote services, configure TLS according to your PostgreSQL/Redis deployment. Keep passwords out of shell history and committed files. The production schema has separate EF migrations for PostgreSQL and SQLite; a database cannot be switched between engines by changing only this setting.

Usage/audit retention runs at startup and hourly. Batch maintenance runs every five seconds, including on instances with zero workers. Retention preserves key and monthly consumption counters and reconciliation receipts. Batch files contain request/response bodies; account for this when setting retention and securing backups.

## Providers

Each built-in provider has `ApiKey`, `ApiKeys`, `ApiKeyCooldownSeconds`, `BaseUrl` and `AllowInsecureHttp` under `LLMProxy:Providers:<section>`. Configuration sections are `OpenAI`, `Anthropic`, `Gemini`, `AzureOpenAI`, `Foundry`, `Mistral`, `Cohere`, `DeepSeek`, `Groq` and `Ollama`. Registry IDs are lowercase, except Azure OpenAI uses `azure`.

| Provider | Default base URL | Credential alias |
| --- | --- | --- |
| OpenAI | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| Anthropic | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| Gemini | `https://generativelanguage.googleapis.com/v1beta` | `GEMINI_API_KEY` |
| Azure | Must be configured | `AZURE_OPENAI_API_KEY` |
| Microsoft Foundry | Must be configured; resource root or `/openai/v1` | `FOUNDRY_API_KEY`, or Entra identity |
| Mistral | `https://api.mistral.ai/v1` | `MISTRAL_API_KEY` |
| Cohere | `https://api.cohere.com/v2` | `COHERE_API_KEY` |
| DeepSeek | `https://api.deepseek.com/v1` | `DEEPSEEK_API_KEY` |
| Groq | `https://api.groq.com/openai/v1` | `GROQ_API_KEY` |
| Ollama | Must be explicitly configured; root or `/v1` | Optional `OLLAMA_API_KEY` |

Credential aliases override the corresponding nested `ApiKey` when set, including an empty value. A nonempty `ApiKeys` list takes precedence over that single key. Every provider also accepts an endpoint alias: replace `_API_KEY` with `_ENDPOINT`, for example `MISTRAL_ENDPOINT` or `FOUNDRY_ENDPOINT`. Blank endpoint aliases retain the default or nested `BaseUrl`, allowing Compose's unused variables to remain empty. `AZURE_OPENAI_API_VERSION` overrides Azure's API version; blank values retain the nested/default version `2024-10-21`.

Foundry settings under `LLMProxy:Providers:Foundry`:

| Setting | Default | Flat alias |
| --- | --- | --- |
| `Authentication` | `ApiKey` | `FOUNDRY_AUTHENTICATION`; `ApiKey` or `EntraId` |
| `TokenScope` | `https://ai.azure.com/.default` | `FOUNDRY_TOKEN_SCOPE` |

`EntraId` uses `DefaultAzureCredential` with managed identity, workload identity or configured Azure credentials. Standard variables include `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET`; workload identity also requires `AZURE_FEDERATED_TOKEN_FILE` and the corresponding token-file mount. An explicit injected `Azure.Core.TokenCredential` can replace the default in custom hosts. No identity token is fetched during readiness checks. [Foundry setup and supported endpoint scope](providers.md#microsoft-foundry).

Azure OpenAI and Foundry mappings contain **deployment names**, which may differ from catalog model IDs. Foundry uses the current OpenAI v1 endpoint without a legacy `api-version` query. Project/agent URLs and Anthropic-on-Foundry endpoints use different protocols and are not accepted by this adapter.

Ollama is enabled by a valid explicit `BaseUrl`, without requiring a key. There is no implicit localhost endpoint. `OLLAMA_ALLOW_INSECURE_HTTP=true` is an alias for `LLMProxy:Providers:Ollama:AllowInsecureHttp`; otherwise HTTP endpoints remain disabled. A supplied Ollama key is sent as a bearer credential. Blank optional boolean/authentication aliases retain nested/default values. [Local and Docker Ollama setup](providers.md#ollama).

`AllowInsecureHttp` defaults to false and should only be enabled for a trusted local adapter or test mock. Provider URLs may not contain embedded credentials, query strings or fragments. Authentication is added in headers. Redirects are not followed.

### Multiple API keys

One provider or named account can hold up to 64 distinct API keys. Configure `ApiKeys` as an array in a private configuration source:

```json
{
  "LLMProxy": {
    "Providers": {
      "OpenAI": {
        "ApiKeys": ["provider-key-1", "provider-key-2", "provider-key-3"],
        "ApiKeyCooldownSeconds": 30
      }
    }
  }
}
```

Supply the real values through your secret/configuration provider or indexed environment variables. For a source process or `docker run --env-file`, the equivalent is:

```dotenv
LLMProxy__Providers__OpenAI__ApiKeys__0=provider-key-1
LLMProxy__Providers__OpenAI__ApiKeys__1=provider-key-2
LLMProxy__Providers__OpenAI__ApiKeys__2=provider-key-3
```

Compose does not forward arbitrary `.env` entries. Add the indexed variables to the `llmproxy` service's `environment` in a Compose override. Named accounts use the same list under, for example, `LLMProxy__Providers__Accounts__openai-east__ApiKeys__0`.

With no list, the existing `ApiKey` and flat credential aliases work as before. With a nonempty list, only its entries are used; the single key is not appended. Empty, duplicate, whitespace-containing or over-8-KiB keys and lists over 64 entries fail startup/reload validation. Entra-authenticated Foundry continues to use identity tokens, and keyless Ollama continues to work.

Requests rotate through keys in round-robin order. All aliases and supported operations for that provider share the pool, including chat, embeddings, Responses, streaming and batch items. Keys share the endpoint, model mappings, prices and provider/account concurrency limit. Use named accounts when endpoints, prices or concurrency scopes must differ. Key rotation and cooldown state are local to each process.

When multiple keys are configured, HTTP 401/403 and 429 responses move to another available key. A 429 switches keys immediately rather than waiting through retries on the rate-limited key. These failures cool the affected key for `ApiKeyCooldownSeconds` (default 30; range 0–3600), or a longer upstream `Retry-After`, capped at 24 hours. Zero disables the default cooldown while still honoring `Retry-After`. Later requests with every key cooling down return `provider_keys_unavailable` (503) with `Retry-After`, or use another provider when model fallback is enabled.

Connection errors, timeouts and HTTP 408/5xx retain bounded HTTP retries before trying another key. Each eligible key is visited at most once per gateway call, with HTTP retry attempts bounded by `Providers:Resilience:RetryCount`; the application request deadline covers the entire pool and provider fallback. Each key has a separate circuit breaker. HTTP 400/422 errors are not replayed across keys. Once a successful HTTP response opens, that response/stream stays on its selected key. Pool failover is independent of the model's `EnableFallback`, which controls switching providers.

Explicit configuration reload validates and replaces the pool for new requests while in-flight work keeps its selected credentials. Reload each replica separately. HTTP attempts expose a `provider_key_id` fingerprint for billing/debugging; raw credentials are never returned or persisted in usage records. See [attempt reporting](expanded-api.md#reports-and-reconciliation).

### Named provider accounts

Add accounts under `LLMProxy:Providers:Accounts`. Each account has `Adapter`, `BaseUrl`, `ApiKey` or `ApiKeys`, `ApiKeyCooldownSeconds` and `AllowInsecureHttp`; Azure accounts also accept `ApiVersion`, and Foundry accounts accept `Authentication` and `TokenScope`. Names contain 1–64 ASCII letters, digits, hyphens or underscores and cannot equal a built-in provider ID. Use the account name in model mappings and prices:

```json
{
  "LLMProxy": {
    "Providers": {
      "Accounts": {
        "openai-east": { "Adapter": "openai", "BaseUrl": "https://api.openai.com/v1" },
        "openai-west": { "Adapter": "openai", "BaseUrl": "https://api.openai.com/v1" }
      }
    },
    "Models": {
      "regional": {
        "Providers": ["openai-east", "openai-west"],
        "ProviderModels": { "openai-east": "gpt-4o-mini", "openai-west": "gpt-4o-mini" },
        "Routing": "latency"
      }
    },
    "Pricing": {
      "openai-east/gpt-4o-mini": { "InputPerMillion": 0.15, "OutputPerMillion": 0.60 },
      "openai-west/gpt-4o-mini": { "InputPerMillion": 0.15, "OutputPerMillion": 0.60 }
    }
  }
}
```

This is an excerpt with illustrative prices. Supply credentials through settings such as `LLMProxy__Providers__Accounts__openai-east__ApiKey`. Accounts have separate HTTP clients, circuits, usage identifiers and provider concurrency scopes. Credentials are not included in the management model listing.

## Models and routing

Every public alias under `LLMProxy:Models` has:

| Field | Default | Meaning |
| --- | --- | --- |
| `Providers` | required | Ordered, unique registered provider names |
| `ProviderModels` | required | Maps each listed provider to its upstream model/deployment |
| `Routing` | `priority` | `priority`, `round-robin`, `random`, `fallback`, `cost`, `latency` or a registered custom strategy |
| `EnableFallback` | `true` | Try other candidates after transient failure; explicit `fallback` always enables this |
| `RequestsPerMinute` | `600` | Shared limit for this alias |
| `MaxOutputTokens` | `4096` | Maximum accepted output cap; 1–1,000,000 |
| `MaxConcurrentRequests` | `100` | Simultaneous requests for the public alias |
| `ProviderCapabilities` | adapter defaults | Optional capability restrictions keyed by provider/account |

Aliases are case-sensitive and limited to 128 characters. Unconfigured providers are skipped; Foundry Entra mode and Ollama have the configuration rules described above. At least one alias needs a configured provider for readiness. Configured aliases without a usable provider are not advertised and return `provider_unavailable` when requested directly.

Default aliases are `fast` (all ten providers), `reasoning` (OpenAI, Anthropic, Foundry, DeepSeek), and `embeddings` (OpenAI, Mistral, Ollama). [Default upstream mappings](providers.md#default-models) are examples; adjust them to match availability in your account. Embedding fallback is disabled by default because providers produce different vector spaces. The gateway does not validate model availability through paid API calls.

Capability selection intersects adapter support with `ProviderCapabilities:<provider>:Features`. Flags include `Chat`, `Tools`, `JsonObject`, `JsonSchema`, `StrictTools`, `RequiredTool`, `NamedTool`, `DisableParallelTools`, `Seed`, `MessageNames`, `ImageInput`, `AudioInput`, `ReasoningEffort`, `ThinkingBudget`, `Embeddings` and `Responses`. Use comma-separated flags in JSON. `TextChat` combines the text/chat/tool flags. Declaring a capability cannot add a feature that the adapter lacks.

Optional target bounds include `ContextWindowTokens`, `MaxTemperature`, `MinTopP`, `MaxTopP`, `JsonWithTools`, `MixedToolStrictness`, `ThinkingWithTools`, `ToolMessageNamesOnly` and `AcceptsResponseFormat`. Known incompatible targets are removed before strategy selection and quota reservation. No compatible target returns `unsupported_model_capability` (400). Context checks use conservative serialized-byte estimates; remote media and provider tokenizers can differ.

`cost` sorts candidates using configured input prices and the requested output ceiling. `latency` uses a process-local exponentially weighted average: normal calls measure completion time, streams measure first-event time, and failed attempts receive a timeout penalty. Unobserved or five-minute-old targets are sampled again. Latency samples are not shared between replicas. Both strategies honor `EnableFallback`.

### Configuration reload

After changing a mounted configuration source, an administrator can call `POST /admin/config/reload`. It reloads and validates models, capabilities, prices and accounts before atomically replacing the active catalog. Invalid configuration returns 400 and preserves the old catalog. In-flight routes retain their provider objects and captured prices. Reload each replica separately; there is no distributed configuration publisher.

Listener, storage, admin/bootstrap secrets, request limits, global concurrency settings, retention, workers and HTTP resilience require restart. Environment variables already supplied to a process cannot be changed by editing the deployment environment alone; restart those processes. Reload validates configuration rather than remote credentials or model availability.

## Pricing

Every target needs a `LLMProxy:Pricing:<provider>/<upstream-model>` entry:

```json
{
  "LLMProxy": {
    "Pricing": {
      "openai/gpt-4o-mini": {
        "InputPerMillion": 0.15,
        "OutputPerMillion": 0.60
      }
    }
  }
}
```

Values are nonnegative USD per million tokens. Zero is allowed for intentionally free/self-hosted models; a missing entry is an error, never implicitly free. Keep prices synchronized with model mappings and your account's actual terms. Optional `CachedInputPerMillion` and `CacheCreationPerMillion` default to the ordinary input rate. `Tiers` contains entries with a unique `FromInputTokens` threshold, input/output prices and optional cache prices. The highest threshold at or below actual input usage applies to the whole request; tiers are not marginal bands. Reservations use the highest applicable rates up to the estimated input ceiling. See [attempt accounting and invoice import](expanded-api.md#reports-and-reconciliation).

Because `:` is a .NET configuration separator, pricing dictionary keys escape colons as `%3A` and literal percent signs as `%25` (escape percent signs first). This rule applies to any provider/model, not just Ollama. For example:

```json
{
  "LLMProxy": {
    "Pricing": {
      "ollama/llama3.1%3A8b": { "InputPerMillion": 0, "OutputPerMillion": 0 }
    }
  }
}
```

The upstream model mapping remains `llama3.1:8b`. Ollama's default zero rate excludes hardware/hosting costs. DeepSeek defaults use peak uncached rates; no time-of-day pricing scheduler is provided. Cached tokens are discounted only when reported in a recognized usage field and a cache rate is configured. Cohere usage prefers actual `usage.tokens`, falling back to `billed_units` only when actual tokens are absent, so estimates can include unbilled tokens.

## Request and resilience limits

| Setting under `LLMProxy` | Default | Meaning |
| --- | --- | --- |
| `Requests:MaxBodyBytes` | `1048576` | Maximum inbound HTTP body; supported configuration range 1 KiB–16 MiB |
| `Requests:MaxMessages` | `256` | Maximum message count |
| `Requests:DefaultMaxOutputTokens` | `1024` | Applied when the client omits a cap, bounded by the model maximum |
| `Requests:TimeoutSeconds` | `180` | Total application deadline, including fallback and streaming |
| `Requests:ReservationTtlSeconds` | `600` | Crash recovery TTL; must exceed request timeout by more than 60 seconds |
| `Providers:Resilience:RetryCount` | `2` | Additional HTTP attempts per selected provider key, 0–5; pooled 429s switch keys immediately |
| `Providers:Resilience:RetryDelaySeconds` | `0.5` | Initial retry delay; exponential backoff and jitter apply |
| `Providers:Resilience:AttemptTimeoutSeconds` | `30` | HTTP attempt deadline through receipt of response headers |
| `Providers:Resilience:TotalTimeoutSeconds` | `100` | Per-key HTTP pipeline deadline, including retries; the application deadline bounds the whole request |
| `RateLimiting:PerUserRequestsPerMinute` | `300` | Shared default limit for all keys with the same owner |
| `RateLimiting:Users:<owner>` | inherited | Per-owner override |
| `Concurrency:GlobalLimit` | `1000` | Global simultaneous inference requests |
| `Concurrency:PerKeyLimit` | `20` | Simultaneous requests per gateway key |
| `Concurrency:PerProviderLimit` | `100` | Simultaneous attempts per provider/account |
| `Batches:MaxFileBytes` | `10485760` | Maximum uploaded batch file; 1 KiB–200 MiB |
| `Batches:MaxRequests` | `1000` | Maximum items per batch; 1–50,000 |
| `Batches:Workers` | `4` | Workers per instance; 0–64, with zero for API-only replicas |

Response bodies are bounded at 8 MiB and upstream SSE events at 1 MiB. There is no retry after streaming output begins. The request deadline bounds inference request-body reads, provider work and downstream writes. Concurrency uses atomic leases in Redis or process-local leases in standalone mode; leases expire after the request deadline plus 60 seconds if a process dies. RPM and concurrency are separate controls. Batch uploads have their own file limit; each batch item's body still obeys `Requests:MaxBodyBytes`.

## HTTP, CORS and security

| Setting | Default | Meaning |
| --- | --- | --- |
| `ASPNETCORE_HTTP_PORTS` | image: `4000` | Listening HTTP port(s); standalone source defaults to `0.0.0.0:4000` |
| `ASPNETCORE_URLS` | unset | Standard ASP.NET Core URL override |
| `LLMProxy:Api:AllowedOrigins` | empty | Explicit HTTP(S) browser origins; credentials/cookies are not enabled |
| `LLMProxy:Api:TrustedProxies` | empty | IP addresses allowed to supply forwarded client-IP/protocol information |
| `LLMProxy:Api:RequireHttps` | `false` | Enable HTTPS redirection/HSTS; use after configuring TLS/proxy trust |
| `LLMProxy:Api:HttpsPort` | `443` | Redirect destination port |
| `LLMPROXY_HEALTH_URL` | derived local `/health/live` | Optional Docker probe URL override |
| `LLMPROXY_ADMIN_KEY` | empty | Shared bootstrap administrator credential; 32–256 characters. Local operator sessions work independently |
| `LLMPROXY_BOOTSTRAP_KEY` | empty | Initial gateway credential; `llmp_sk_` plus 32–128 URL-safe random characters |
| `LLMPROXY_BOOTSTRAP_OWNER` | `bootstrap` | Initial key owner |

An initial bootstrap key has access to all configured aliases and a 60 RPM key limit. Use the CLI or management API for narrower scopes or budgets. Existing bootstrap policies are never overwritten on restart. A changed bootstrap value creates an additional key; revoke the old one explicitly.

Key expiry, UTC monthly allowances and rotation are stored per key through the CLI/management API, rather than in server configuration. `/admin` serves the dashboard. [Operator roles, sessions and key policy](expanded-api.md#operator-access-and-dashboard).

Example CORS override: `LLMProxy__Api__AllowedOrigins__0=https://app.example.com`. `LLMProxy:Api:AdminKey` may alternatively be supplied by a secure .NET configuration provider; the flat alias takes precedence.

## Complete JSON replacement

To edit arrays, remove default aliases or change an entire routing layout, mount a complete replacement for `/app/appsettings.json`. .NET merges configuration keys across sources; setting only `Providers__0` does not remove higher array indexes from a JSON file.

You can extract the built-in file without cloning source:

```bash
docker create --name llmproxy-config mohammedtv/llmproxy:0.2.0
docker cp llmproxy-config:/app/appsettings.json ./appsettings.json
docker rm llmproxy-config
# Edit the local file, preserving Logging and relevant Pricing entries.
docker run -d --name llmproxy -p 4000:4000 --env-file .env \
  -v llmproxy-data:/data \
  -v "$PWD/appsettings.json:/app/appsettings.json:ro" \
  mohammedtv/llmproxy:0.2.0
```

For Compose, put the mount in a local `compose.override.yml`. Credentials should remain in the environment or a secure configuration source, not in the replacement JSON.

## Observability and Compose-only variables

`OTEL_EXPORTER_OTLP_ENDPOINT` enables OTLP traces and metrics. Standard OpenTelemetry variables such as `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES` and sampler settings are supported by the SDK. See [operations](operations.md).

The Compose file consumes `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `LLMPROXY_IMAGE`, `LLMPROXY_PORT` (host port) and `LLMPROXY_AUTO_MIGRATE`. These are Compose inputs, not additional server option aliases. It forwards the provider/bootstrap/admin/OTLP endpoint variables from `.env`; pass other server settings explicitly through a Compose override.

`LLMPROXY_IMAGE` defaults to the public `mohammedtv/llmproxy:0.2.0` image. Use a full version or digest to pin a deployment; `latest` follows successful main builds and stable releases. See [image tags and release verification](releases.md).
