# LLMProxy

Self-hosted, OpenAI-compatible LLM gateway built with C# and .NET 10. Applications use one base URL and gateway API keys; LLMProxy manages upstream credentials, model routing, quotas and usage accounting. MIT licensed.

[Source and full README](https://github.com/Mo7ammedd/LLMProxy) · [Releases and upgrades](https://github.com/Mo7ammedd/LLMProxy/blob/main/CHANGELOG.md) · [Configuration](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/configuration.md) · [API reference](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/api.md) · [Operations](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/operations.md)

## Images

| Tag | Platforms | Contents |
| --- | --- | --- |
| `0.2.0` | `linux/amd64`, `linux/arm64` | Version 0.2.0, including multiple keys per provider |
| `latest` | `linux/amd64`, `linux/arm64` | Successful main builds and stable releases; use a full version or digest to pin a deployment |

Version 0.2.0 is built from [commit 1911fc8](https://github.com/Mo7ammedd/LLMProxy/commit/1911fc881e39bbe033a7fa57a4d7ce9573a05c6c). The image passed the repository's [CI checks](https://github.com/Mo7ammedd/LLMProxy/actions/runs/35991763024), including native AMD64 and ARM64 container and SDK integration tests.

```bash
docker pull mohammedtv/llmproxy:0.2.0
```

Version 0.2.0's multi-platform digest is `sha256:a8bd1b3e93b12549733b86fbb0be9809704924e2a2e27bee075df4c7ab56d3db`. GitHub source tags have a `v` prefix, while Docker Hub version tags omit it. See the [release guide](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/releases.md) for tag conventions and verified builds.

## Quick start

Create a local `.env` file containing at least one upstream credential:

```dotenv
OPENAI_API_KEY=your-provider-key
```

Run the gateway with persistent SQLite storage:

```bash
docker run -d \
  --name llmproxy \
  --restart unless-stopped \
  -p 4000:4000 \
  --env-file .env \
  -v llmproxy-data:/data \
  mohammedtv/llmproxy:0.2.0

docker exec llmproxy dotnet LLMProxy.Server.dll keys create \
  --owner my-app --models fast,reasoning,embeddings --rpm 60
```

The CLI returns a JSON object containing a one-time gateway key beginning with `llmp_sk_`. Store that key for your application. Upstream provider keys stay on the gateway.

```bash
export LLMPROXY_API_KEY='the-key-returned-by-keys-create'
curl http://localhost:4000/v1/models \
  -H "Authorization: Bearer $LLMPROXY_API_KEY"
```

Use **`http://localhost:4000/v1`** as the OpenAI SDK base URL and your gateway key as the API key. Model aliases and capabilities depend on configured providers; unavailable aliases are omitted from `/v1/models`. See the [Python, TypeScript and C# examples](https://github.com/Mo7ammedd/LLMProxy#openai-sdk-usage).

## Multiple keys per provider

Every API-key adapter supports up to 64 distinct keys per provider account. For example, put this in the `.env` passed to `docker run`:

```dotenv
LLMProxy__Providers__OpenAI__ApiKeys__0=provider-key-1
LLMProxy__Providers__OpenAI__ApiKeys__1=provider-key-2
LLMProxy__Providers__OpenAI__ApiKeyCooldownSeconds=30
```

A nonempty pool replaces the single `OPENAI_API_KEY`. Requests rotate among available keys. Authentication failures and rate limits switch to another key and cool the affected credential; transient failures use bounded retries before failover. Each key has a separate circuit breaker. Rotation and cooldowns are local to each gateway process.

Named provider accounts support separate endpoints, credentials, prices and concurrency scopes for the same adapter. Compose requires these indexed variables to be explicitly forwarded in a service environment override. See [pool configuration and Compose examples](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/configuration.md#multiple-api-keys).

## Features

| Area | Implemented behavior |
| --- | --- |
| Providers | OpenAI, Anthropic, Google Gemini, Azure OpenAI, Microsoft Foundry, Mistral, Cohere, DeepSeek, Groq and Ollama |
| Inference | Chat Completions, SSE streaming, tools and tool results, structured output, embeddings and stateless Responses where supported |
| Media and reasoning | Image/audio chat input, Responses image input, reasoning effort and thinking budgets according to adapter/model capabilities |
| Routing | Public model aliases, six selection strategies, capability checks, provider fallback, named accounts and explicit configuration reload |
| Limits and keys | Key permissions, expiry, disabling and rotation; per-key/owner/model RPM, concurrency limits, lifetime and monthly token/USD allowances |
| Accounting | Usage and cost reports, configurable cache/tier prices, HTTP attempt records, credential fingerprints and invoice reconciliation |
| Batches | Durable files and batches, distributed worker claims, cancellation, expiry and partial results |
| Administration | Embedded `/admin` UI, local administrator/operator/auditor roles, sessions, audit history, usage charts, filters and CSV export |
| Operations | SQLite or PostgreSQL storage, Redis coordination, migrations, retention, health/readiness, graceful shutdown, OpenTelemetry and backup/recovery tooling |

Provider support differs by operation and model. Consult the [compatibility matrix](https://github.com/Mo7ammedd/LLMProxy#provider-compatibility) and [API boundaries](https://github.com/Mo7ammedd/LLMProxy#compatibility-boundaries). Responses is stateless and supported through OpenAI/Foundry. Files are for gateway batches. Image generation, speech generation/transcription and legacy Completions are not exposed.

## Docker Compose with PostgreSQL and Redis

Obtain the repository and copy `.env.example` to `.env`. Set provider credentials, separate `POSTGRES_PASSWORD` and `REDIS_PASSWORD` values, and this image setting:

```dotenv
LLMPROXY_IMAGE=mohammedtv/llmproxy:0.2.0
```

From the checkout:

```bash
docker compose pull
docker compose up -d --no-build
docker compose exec llmproxy dotnet LLMProxy.Server.dll keys create \
  --owner my-app --models fast,reasoning,embeddings --rpm 60 \
  --monthly-token-limit 1000000 --monthly-budget 20
curl http://localhost:4000/health/ready
```

Compose runs PostgreSQL 16 and Redis 7 with persistent volumes and publishes only the gateway port. PostgreSQL plus Redis is required for multiple gateway replicas. The standalone SQLite volume is intended for one instance.

## Container configuration

| Setting | Default or purpose |
| --- | --- |
| HTTP port | `4000`; override with `ASPNETCORE_HTTP_PORTS` and matching port mapping |
| Persistent data | `/data`, containing `llmproxy.db` in standalone mode |
| Runtime user | Non-root UID/GID `1654:1654`; bind-mounted data must be writable by this user |
| Base image | .NET 10 ASP.NET Core Ubuntu Noble chiseled-extra; no shell in the runtime image |
| `LLMProxy__Storage__Mode` | `Standalone` (default) or `PostgreSql` |
| `ConnectionStrings__Postgres` | PostgreSQL connection string when using PostgreSQL mode |
| `ConnectionStrings__Redis` | Redis connection string when using PostgreSQL mode |
| `LLMPROXY_BOOTSTRAP_KEY` | Optional gateway bootstrap key: `llmp_sk_` followed by at least 32 random URL-safe characters |
| `LLMPROXY_ADMIN_KEY` | Separate bootstrap administrator secret, at least 32 characters |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Optional collector for traces and metrics |

Set `LLMPROXY_ADMIN_KEY` before starting the container, then open `/admin` to create the first local administrator. Local operator accounts continue to work if the shared bootstrap secret is later removed.

`/health/live` checks the process; `/health/ready` checks storage, Redis, provider configuration and shutdown state. Health checks do not call a live LLM API. The image includes a Docker health check. CLI commands include `keys create`, `migrate`, `reservations recover` and `healthcheck`.

For production, pin a version or digest, persist and back up the database, configure TLS at a trusted reverse proxy, and use the [operations guide](https://github.com/Mo7ammedd/LLMProxy/blob/main/docs/operations.md) for SSE proxy settings, migrations, telemetry and recovery.
