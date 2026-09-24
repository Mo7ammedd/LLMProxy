# Remaining production work

The core gateway is implemented: OpenAI-compatible text/tool Chat Completions and model listing, ten provider adapters, normal/SSE responses, four routing strategies, resilient retry/fallback, hashed gateway keys, permissions, distributed RPM limits, quota reservations, usage/cost accounting, PostgreSQL/Redis, health/telemetry, Docker/Compose and tested GHCR publication. It remains an MVP with specific operational and product limits.

## Highest priorities

| Area | Implemented | Remaining work |
| --- | --- | --- |
| Capability-aware routing | Priority, round-robin, random and fallback over configured providers | Declare model capabilities and exclude incompatible targets before selecting or reserving quota. Requests requiring JSON schemas or forced tools can currently select a provider that rejects them. Cost/latency-aware routing and configurable concurrency limits are also absent. |
| Operator access and key lifecycle | Separate shared admin secret, key creation/list/update/disable, owner/model/usage policies | Operator identities, SSO/RBAC, management audit events, key expiry and a rotation workflow. There is no management dashboard. A new key can be created and the old one disabled manually. |
| Billing and quota policy | Lifetime token/spending allowances, atomic admission reservations, estimated costs and crash recovery | Periodic/monthly allowances, tier/cache-aware prices, accounting for individual upstream attempts and reconciliation against provider invoices. Current budgets are estimates, not billing guarantees. |
| Operational validation | Real PostgreSQL/Redis integration tests, concurrent reservation/rate-limit tests, Docker persistence/shutdown checks, multi-architecture image builds | Sustained-load and long-stream benchmarks, multi-instance failure/partition exercises, automated backup/restore drills and ARM64 runtime tests. Container runtime tests currently execute on amd64. Define operational targets and verify them under expected load before a large deployment. |

## Other gaps

| Area | Current boundary | Next useful addition |
| --- | --- | --- |
| Usage and audit | Every admitted chat request is recorded. Authentication, validation, RPM and quota-admission rejections have HTTP telemetry but no durable completion row. The admin listing is capped at 1,000 records. | Cursor pagination, time/owner filters, exports, rollups, retention jobs and a separate durable audit stream for rejected/admin actions. |
| Configuration and provider accounts | Strongly typed configuration is read at startup. Each built-in provider has one credential/endpoint configuration. Readiness checks local configuration, not remote model access. | Validated configuration reload, named accounts/regions per adapter, explicit per-model capabilities and optional operator-triggered connectivity validation. |
| Release hardening | Dependency lock files, non-root minimal images, CI-gated publication and BuildKit provenance | Explicit image signing/verification, SBOM publication, vulnerability scanning, automated dependency/base-image updates and pinned action revisions. |
| API breadth | Text/function-tool `/v1/chat/completions` and `/v1/models`, one choice per request | Responses, embeddings, images/audio/video, batches, log probabilities and broader reasoning controls. These extend the original MVP scope. |
| Foundry coverage | OpenAI v1 chat deployments, API-key/Entra auth, deployment-name routing | Foundry Agents/projects, Responses and the separate Anthropic-on-Foundry API need their own protocol support. |
| Optional packages | Reusable class libraries within the solution | Versioned NuGet packages and package-release workflows, if demanded by integrators. Normal gateway deployment does not require them. |

Provider support does not imply every provider-specific feature is exposed. In particular, Cohere retrieval/citations, provider thinking controls and capability-aware fallback remain outside the common request contract. The [compatibility matrix](api.md#compatibility) describes the supported behavior and explicit rejections.

## Distribution status

The repository and GHCR image are private as requested. Pulling the image requires a GitHub identity with repository/package access. The source carries the MIT license, but anonymous public source/image distribution requires a separate visibility change. A successful `main` pipeline publishes `latest` and a commit tag; stable versioned releases require an explicit version tag.

## Recommended next increment

Add a provider/model capability registry and route by required request features first. This makes mixed-provider aliases more dependable as provider coverage grows. Follow with operator authentication/auditing and periodic quota/reconciliation work, alongside load and recovery testing. Keep prompts and sensitive request content out of the default audit/telemetry data.
