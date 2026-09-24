# Gateway expansion

Scope requested: implement the eight gaps identified in the repository review.

- [x] Request and model capability routing, including feature combinations.
- [x] Operator login and roles, durable audit, key expiry and rotation.
- [x] UTC monthly allowances, cache/tier pricing, upstream attempts and invoice reconciliation.
- [x] Paginated reporting, filters, CSV export, dashboard and retention.
- [x] Named provider accounts and validated runtime configuration reload.
- [x] Price/latency routing and distributed concurrency leases.
- [x] Embeddings, Responses, image/audio input, reasoning controls and durable batches.
- [x] Load/recovery/backup validation and native ARM64 runtime CI configuration.

Each increment includes relevant migrations, tests and API documentation. Provider calls in automated validation use deterministic mocks. Existing Chat Completions clients and lifetime allowances remain supported.

## Verified on 2026-09-24

- Locked dependency restore and Release build: no warnings or errors, using .NET SDK 10.0.401.
- 254 tests passed: 60 unit, 127 provider and 67 integration, with PostgreSQL and Redis enabled and no skips.
- SQLite/PostgreSQL migrations exercised; EF reports no pending model changes for either context. Legacy usage is labeled as chat, and recovered requests retain their operation.
- Formatting verification, Git whitespace checks, Python/JavaScript syntax, OpenAPI 3.1 validation, documentation links/JSON examples and Actionlint passed.
- Python, TypeScript and C# SDK chat/SSE smoke passed. Python/TypeScript embeddings and Responses/SSE passed; Python files/batches passed.
- Chromium desktop/mobile checks passed for login/error handling, charts, CSV, pagination/filters, key creation/edit/rotation, operator creation, audit, reload and auditor controls, with no JavaScript or CSP errors.
- Two-instance recovery drill: 21,394/21,394 requests over 60.015 seconds, 356.479 requests/second, 39.91 ms p95 latency and 28.10 ms p95 first-token latency. A 30.125-second stream completed; crash recovery charged the orphan once, Redis loss failed admission closed and recovered, and a PostgreSQL backup restored with matching keys, usage, monthly counters, attempts and audit. Measurements use local mocks, not production providers.

Generated reports/screenshots are under `artifacts/`. The container daemon crashes in this workspace; local Docker runtime checks and the remote ARM64 job have not run. CI includes native ARM64 and AMD64 container/SDK jobs and gates publication on them. No image or release was published as part of local verification.

## Multiple keys per provider

Additional scope requested: allow one provider to use multiple upstream API keys.

- [x] `ApiKeys` arrays for every built-in adapter and named account, preserving existing `ApiKey` configuration.
- [x] Concurrent round-robin selection, balanced selection among healthy keys, bounded failover, cooldowns and separate circuits per key.
- [x] Shared pools for chat, embeddings, Responses, streams and batch execution; streams keep their selected credential.
- [x] Validated reload with credentials retained by in-flight requests and a shared provider concurrency limit.
- [x] Credential fingerprints on individual HTTP attempts, with additive SQLite/PostgreSQL migrations and existing invoice reconciliation.
- [x] Configuration, environment examples and API documentation.

Verification on 2026-09-24: **327 tests passed** (60 unit, 189 provider, 78 integration), including 73 new pool tests. PostgreSQL and Redis were enabled; there were no failures or skips. Release compilation, formatting, OpenAPI/documentation checks and EF model/migration consistency checks passed for both databases. Provider traffic used mocks.

## Deliberate boundaries

Operator identities are local; SSO/MFA are follow-up work. Responses is stateless and excludes stored/background responses, hosted tools and provider file references. Batches use gateway workers and ordinary pricing. Latency samples and provider key rotation/cooldowns are process-local, reload applies separately to each replica, and live-provider compatibility/pricing still depends on deployment configuration. See [extended APIs](expanded-api.md) and the [remaining roadmap](roadmap.md).
