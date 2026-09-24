# Configuration reference

The server reads `appsettings.json`, environment-specific .NET configuration and environment variables. Settings are strongly typed and validated at startup. Restart after changes. Never put credentials in committed JSON. Environment names are case-sensitive on Linux; .NET configuration keys are case-insensitive.

## Storage

| Setting | Default | Meaning |
| --- | --- | --- |
| `LLMProxy:Storage:Mode` | `Standalone` | `Standalone` uses SQLite; `PostgreSql` requires PostgreSQL and Redis |
| `LLMProxy:Storage:SqlitePath` | `data/llmproxy.db` | Relative to the server working directory; image sets `/data/llmproxy.db` |
| `LLMProxy:Storage:AutoMigrate` | `true` | Apply database migrations during startup |
| `LLMProxy:Storage:RedisKeyPrefix` | `llmproxy` | Shared rate/cursor namespace across replicas; braces are prohibited |
| `ConnectionStrings:Postgres` | unset | Npgsql connection string |
| `ConnectionStrings:Redis` | unset | StackExchange.Redis connection string |

Environment example:

```dotenv
LLMProxy__Storage__Mode=PostgreSql
ConnectionStrings__Postgres=Host=postgres;Database=llmproxy;Username=llmproxy;Password=REPLACE
ConnectionStrings__Redis=redis:6379,password=REPLACE,abortConnect=false
```

For remote services, configure TLS according to your PostgreSQL/Redis deployment. Keep passwords out of shell history and committed files. The production schema has separate EF migrations for PostgreSQL and SQLite; a database cannot be switched between engines by changing only this setting.

## Providers

Each built-in provider has `ApiKey`, `BaseUrl` and `AllowInsecureHttp` under `LLMProxy:Providers:<name>`. Names in this section are `OpenAI`, `Anthropic`, `Gemini` and `AzureOpenAI`; registry names are `openai`, `anthropic`, `gemini` and `azure`.

| Provider | Default base URL | Credential alias |
| --- | --- | --- |
| OpenAI | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| Anthropic | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| Gemini | `https://generativelanguage.googleapis.com/v1beta` | `GEMINI_API_KEY` |
| Azure | Must be configured | `AZURE_OPENAI_API_KEY` |

Credential aliases override the corresponding nested value when set, including an empty value. `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_API_VERSION` override Azure's base URL and API version. Azure's default version is `2024-10-21`; mappings must contain deployment names, not a provider catalog ID unless those names happen to match.

`AllowInsecureHttp` defaults to false and should only be enabled for a trusted local adapter or test mock. Provider URLs may not contain embedded credentials, query strings or fragments. Authentication is added in headers. Redirects are not followed.

## Models and routing

Every public alias under `LLMProxy:Models` has:

| Field | Default | Meaning |
| --- | --- | --- |
| `Providers` | required | Ordered, unique registered provider names |
| `ProviderModels` | required | Maps each listed provider to its upstream model/deployment |
| `Routing` | `priority` | `priority`, `round-robin`, `random`, `fallback` or a registered custom strategy |
| `EnableFallback` | `true` | Try other candidates after transient failure; explicit `fallback` always enables this |
| `RequestsPerMinute` | `600` | Shared limit for this alias |
| `MaxOutputTokens` | `4096` | Maximum accepted output cap; 1–1,000,000 |

Aliases are case-sensitive and limited to 128 characters. A provider without configured credentials is skipped. At least one alias needs a configured provider for readiness. Configured aliases unavailable with the supplied credentials are not advertised and return `provider_unavailable` when requested directly.

Default aliases are `fast` and `reasoning`; adjust upstream models to match provider availability in your account. The gateway does not validate model availability by making a paid API call at startup.

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

Values are nonnegative USD per million tokens. Zero is allowed for intentionally free/self-hosted models; a missing entry is an error, never implicitly free. Keep prices synchronized with model mappings and your account's actual terms. Flat input/output pricing does not represent every caching, batch, reasoning, regional or long-context discount/tier.

## Request and resilience limits

| Setting under `LLMProxy` | Default | Meaning |
| --- | --- | --- |
| `Requests:MaxBodyBytes` | `1048576` | Maximum inbound HTTP body; supported configuration range 1 KiB–16 MiB |
| `Requests:MaxMessages` | `256` | Maximum message count |
| `Requests:DefaultMaxOutputTokens` | `1024` | Applied when the client omits a cap, bounded by the model maximum |
| `Requests:TimeoutSeconds` | `180` | Total application deadline, including fallback and streaming |
| `Requests:ReservationTtlSeconds` | `600` | Crash recovery TTL; must exceed request timeout by more than 60 seconds |
| `Providers:Resilience:RetryCount` | `2` | Additional HTTP attempts per provider, 0–5 |
| `Providers:Resilience:RetryDelaySeconds` | `0.5` | Initial retry delay; exponential backoff and jitter apply |
| `Providers:Resilience:AttemptTimeoutSeconds` | `30` | HTTP attempt deadline through receipt of response headers |
| `Providers:Resilience:TotalTimeoutSeconds` | `100` | Per-provider HTTP pipeline deadline, including retries |
| `RateLimiting:PerUserRequestsPerMinute` | `300` | Shared default limit for all keys with the same owner |
| `RateLimiting:Users:<owner>` | inherited | Per-owner override |

Response bodies are bounded at 8 MiB and upstream SSE events at 1 MiB. There is no retry after streaming output begins. The application deadline also bounds response-body reads.

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
| `LLMPROXY_ADMIN_KEY` | empty | Management API credential; 32–256 characters |
| `LLMPROXY_BOOTSTRAP_KEY` | empty | Initial gateway credential; `llmp_sk_` plus 32–128 URL-safe random characters |
| `LLMPROXY_BOOTSTRAP_OWNER` | `bootstrap` | Initial key owner |

An initial bootstrap key has access to all configured aliases and a 60 RPM key limit. Use the CLI or management API for narrower scopes or budgets. Existing bootstrap policies are never overwritten on restart. A changed bootstrap value creates an additional key; revoke the old one explicitly.

Example CORS override: `LLMProxy__Api__AllowedOrigins__0=https://app.example.com`. `LLMProxy:Api:AdminKey` may alternatively be supplied by a secure .NET configuration provider; the flat alias takes precedence.

## Complete JSON replacement

To edit arrays, remove default aliases or change an entire routing layout, mount a complete replacement for `/app/appsettings.json`. .NET merges configuration keys across sources; setting only `Providers__0` does not remove higher array indexes from a JSON file.

You can extract the built-in file without cloning source:

```bash
docker create --name llmproxy-config ghcr.io/mo7ammedd/llmproxy:latest
docker cp llmproxy-config:/app/appsettings.json ./appsettings.json
docker rm llmproxy-config
# Edit the local file, preserving Logging and relevant Pricing entries.
docker run -d --name llmproxy -p 4000:4000 --env-file .env \
  -v llmproxy-data:/data \
  -v "$PWD/appsettings.json:/app/appsettings.json:ro" \
  ghcr.io/mo7ammedd/llmproxy:latest
```

For Compose, put the mount in a local `compose.override.yml`. Credentials should remain in the environment or a secure configuration source, not in the replacement JSON.

## Observability and Compose-only variables

`OTEL_EXPORTER_OTLP_ENDPOINT` enables OTLP traces and metrics. Standard OpenTelemetry variables such as `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_RESOURCE_ATTRIBUTES` and sampler settings are supported by the SDK. See [operations](operations.md).

The Compose file consumes `POSTGRES_PASSWORD`, `REDIS_PASSWORD`, `LLMPROXY_IMAGE`, `LLMPROXY_PORT` (host port) and `LLMPROXY_AUTO_MIGRATE`. These are Compose inputs, not additional server option aliases. It forwards the provider/bootstrap/admin/OTLP endpoint variables from `.env`; pass other server settings explicitly through a Compose override.
