# Operations

## Deployment modes

Use the Compose stack, or connect the image to external PostgreSQL and Redis, for production and multiple replicas. Keep model mappings, pricing and Redis namespace consistent across replicas. A single standalone container stores data in SQLite under `/data`; mount a named volume and do not share that SQLite file across running gateway processes.

The image uses a multi-stage build and .NET's minimal chiseled runtime. It runs as UID/GID `1654:1654` with no shell or package manager. Compose also disables additional capabilities and uses a read-only root filesystem. Use `docker exec llmproxy dotnet LLMProxy.Server.dll ...` for CLI operations rather than expecting `/bin/sh` in the image.

For a different internal port:

```bash
docker run -d --name llmproxy -p 8080:8080 --env-file .env \
  -e ASPNETCORE_HTTP_PORTS=8080 -v llmproxy-data:/data \
  ghcr.io/mo7ammedd/llmproxy:latest
```

The built-in health command derives its port from `ASPNETCORE_HTTP_PORTS` or HTTP `ASPNETCORE_URLS`. For unusual bindings or HTTPS-only listeners, supply a reachable `LLMPROXY_HEALTH_URL`.

## Health and shutdown

| Endpoint | Meaning |
| --- | --- |
| `/health/live` | Process is serving requests; suitable for container liveness |
| `/health/ready` | Storage schema reachable, Redis reachable when required, at least one usable model, not shutting down |
| `/health` | Alias of readiness |

Readiness JSON identifies `postgresql` or `sqlite`, `redis` when used, `provider_configuration` and `accepting_requests`. An unavailable dependency yields HTTP 503. Partially configured aliases yield `Degraded` with HTTP 200 and are excluded from `/v1/models`. Provider health checks inspect configuration only; provider service outages are handled by routing/resilience, not process restart loops.

`SIGTERM` triggers ASP.NET Core's graceful shutdown. The host allows 45 seconds for draining; Compose grants 60 seconds before force-killing the container. Readiness turns unhealthy while stopping. Longer outstanding requests are cancelled, and durable quota reservations protect accounting if finalization cannot run. Migrations execute before the HTTP server begins listening; an unreachable startup database prevents a successful startup.

## TLS and reverse proxies

Terminate TLS with your ingress/proxy, or use standard Kestrel certificate settings. Example direct TLS configuration:

```dotenv
ASPNETCORE_URLS=https://0.0.0.0:4443
ASPNETCORE_Kestrel__Certificates__Default__Path=/certs/server.pfx
ASPNETCORE_Kestrel__Certificates__Default__Password=FROM_YOUR_SECRET_STORE
LLMPROXY_HEALTH_URL=https://localhost:4443/health/live
```

Mount the certificate read-only with permissions allowing UID 1654 to read it. The health client validates certificates, so use a trusted certificate/CA and a matching host, or retain a local HTTP listener for health probes.

Behind a reverse proxy, set `LLMProxy__Api__TrustedProxies__0` to the proxy's actual IP before enabling `LLMProxy__Api__RequireHttps=true`. Only configured proxies are trusted for `X-Forwarded-For` and `X-Forwarded-Proto`. Do not blanket-trust arbitrary forwarded headers.

Example nginx location behind an HTTPS server block:

```nginx
location / {
    proxy_pass http://llmproxy:4000;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;
    proxy_cache off;
    proxy_read_timeout 240s;
}
```

Protect `/admin/*` with network policy or an operator-only ingress in addition to its separate bearer secret. Set explicit `AllowedOrigins` only for browser applications that should call the gateway. TLS termination at the ingress works without application-level redirects; if redirects are enabled, also configure health probes appropriately.

## Observability

JSON console logs include request/correlation IDs, public model, selected provider, status, token totals and latency. The gateway does not enable HTTP body logging, EF sensitive-data logging or provider response logging. Avoid enabling these through custom middleware; raw prompts and keys must not be recorded.

Set `OTEL_EXPORTER_OTLP_ENDPOINT`, e.g. `http://collector:4317`, to export traces and metrics through OTLP. No collector is required when this is unset. A collector can route metrics to Prometheus/Grafana and traces to Tempo, Jaeger or another backend.

Activity source and meter: `LLMProxy`. Spans include the ASP.NET request, gateway orchestration and provider operations. Request and provider model attributes are included, with no prompt or API-key attributes. Streaming provider-call spans measure preparation to the first event; the full stream is covered by the gateway/request span and full provider-duration metric.

| Metric | Meaning |
| --- | --- |
| `llmproxy.requests` | Completed/admitted request count, tagged by model/provider/status |
| `llmproxy.errors` | Failed/cancelled admitted request count |
| `llmproxy.request.duration` | Total request duration, seconds |
| `llmproxy.provider.requests` / `.errors` | Provider attempt outcomes after internal HTTP resilience |
| `llmproxy.provider.duration` | Provider duration through full response/stream, seconds |
| `llmproxy.provider.first_token` | Stream preparation/first-event latency, seconds |
| `llmproxy.tokens` | Input/output token count, tagged by direction |
| `llmproxy.estimated_cost` | Estimated USD consumption |
| `llmproxy.fallbacks` | Provider fallback count |

ASP.NET Core and HttpClient instrumentation additionally capture HTTP duration/status/connection metrics, including authentication and admission failures. HTTP retries inside one provider attempt can be inspected through HttpClient telemetry. Request IDs are log/span fields, not metric labels, to avoid high cardinality.

## Migrations and upgrades

For controlled upgrades, back up data, apply migrations once and then roll out server instances:

```bash
docker compose run --rm llmproxy migrate
# Set LLMPROXY_AUTO_MIGRATE=false in .env once your migration job owns schema changes.
docker compose pull
docker compose up -d
```

Run the migration command with the **new image version** before starting that version's instances. Production runtime credentials can have narrower privileges than the migration role. Startup uses EF Core migration coordination; PostgreSQL/SQLite migration histories are independent.

Pin a version or image digest instead of `latest` for reproducible deployments. Review schema compatibility before rolling back an image; database migrations are not automatically reversed.

## Backups and retention

PostgreSQL stores hashes/policies, counters, reservations and usage. Back it up with your usual encrypted PostgreSQL backup strategy and test restoration. For the Compose default:

```bash
docker compose exec -T postgres pg_dump -U llmproxy llmproxy > llmproxy-backup.sql
```

Treat backups as sensitive: they contain account ownership and activity metadata even though prompts and raw keys are absent. Redis uses AOF with `appendfsync everysec`; a sudden Redis failure may lose a small recent rate-limit window. Lifetime quotas remain in PostgreSQL. Use a highly available Redis deployment and stronger durability if your RPM policy requires it.

The MVP does not automatically delete usage. Apply an explicit retention policy in PostgreSQL according to your needs. Removing old usage rows does not reset per-key consumed totals. Do not delete active reservations or reset counters to resolve transient errors. Investigate `abandoned` records and reconcile conservatively charged requests before making controlled administrative adjustments.

For standalone SQLite, stop the container before copying the database or use a proper SQLite online backup. Copying only a live `.db` file while ignoring its WAL can produce an inconsistent backup.

## Failure interpretation

- Redis unavailable: chat/list admissions fail closed with 503; liveness remains healthy.
- PostgreSQL unavailable: authentication/accounting cannot proceed; readiness reports the storage dependency. A usage finalization failure retains its reservation for recovery.
- All providers failing: the request returns a sanitized upstream error after configured retry/fallback. This does not make liveness fail.
- Interrupted streams or unknown outcomes: inspect `usage_estimated` and `error_code`; estimates may consume the reserved ceiling to avoid cancellation-based quota bypass.
- Insufficient quota on a short prompt: lower the output cap or increase the lifetime allowance; admission includes conservative input estimation and the full output ceiling.

See [accounting and recovery](architecture.md#accounting-and-failure-recovery) for the precise behavior.
