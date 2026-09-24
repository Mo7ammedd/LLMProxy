# Changelog

User-visible changes are grouped by release. Git tags use `vMAJOR.MINOR.PATCH`; Docker Hub version tags omit the leading `v`. See the [release guide](docs/releases.md) for image tags, digests and publication checks.

## Unreleased

- Make Docker Hub the default for pinned Docker/Compose quick starts and document image provenance and upgrades.
- Publish verified AMD64/ARM64 builds to Docker Hub and GHCR from CI, verify both registry manifests and maintain the Docker Hub overview from source.
- Add release/build/image/license badges, a release guide, bug and feature forms, and a pull request template.

## [0.2.0] — 2026-09-24

### Added

- Six provider adapters: Microsoft Foundry, Mistral, Cohere, DeepSeek, Groq and Ollama, bringing the total to ten. Foundry supports API keys and Entra ID; trusted local Ollama endpoints have an explicit HTTP opt-in.
- Up to 64 API keys per provider account, rotation, authentication/rate-limit failover, cooldowns, separate credential circuits and credential fingerprints in attempt reports. Named accounts support separate endpoints, prices and concurrency scopes for the same adapter.
- Adapter/model capability checks, feature-combination validation, context limits, cost and latency routing, and explicit reload of models, pricing, accounts and key pools.
- Embeddings, stateless Responses on OpenAI/Foundry, supported image/audio inputs and reasoning controls, plus durable files and batches with worker claims, cancellation, expiry and partial results.
- Local administrator/operator/auditor identities, expiring sessions, an embedded `/admin` console and management/rejection audit records.
- Gateway key expiry and rotation with optional grace, UTC monthly token/USD allowances, shared concurrency admission and request deadlines.
- Usage charts, filtered/cursor reports and CSV, ordinary/cache/tier pricing, individual upstream attempts and idempotent invoice reconciliation.
- Usage/audit/batch retention, SQLite and PostgreSQL migrations, load and multi-instance recovery drills, Redis-loss checks, database restore checks and native ARM64 container/SDK CI.
- Public Docker Hub images for `linux/amd64` and `linux/arm64`, with multi-platform digest `sha256:a8bd1b3e93b12549733b86fbb0be9809704924e2a2e27bee075df4c7ab56d3db` for `mohammedtv/llmproxy:0.2.0`.

### Compatibility notes

- Single provider keys continue to work. A nonempty `ApiKeys` list replaces the single key; it does not append to it. Rotation and cooldowns are local to each gateway process.
- Responses is stateless. Files serve gateway batches, which execute ordinary requests at ordinary configured prices. See the [protocol boundaries](README.md#compatibility-boundaries) for unsupported stateful, hosted-tool and media-generation operations.
- Role scopes apply across the gateway. OIDC/SSO, MFA and owner-scoped operator permissions remain follow-up work.
- Capability validation rejects unsupported request combinations before provider execution. Custom model catalogs need suitable capabilities, aliases, upstream mappings and prices to expose the new protocols.
- Batch inputs and outputs persist request/response content. Include them in backup, access and retention planning even though ordinary usage logs omit bodies.

### Upgrading from 0.1.0

1. Record the deployed image digest and configuration. Drain or stop the old gateway replicas, then back up PostgreSQL or the complete SQLite data directory using the [backup guidance](docs/operations.md#backups-and-retention).
2. Set `LLMPROXY_IMAGE=mohammedtv/llmproxy:0.2.0` in the Compose `.env`, or select that image in your deployment. Keep the existing data volumes, connection strings, Redis namespace and provider credentials.
3. Apply the new `GatewayExpansion` and `ProviderKeyPools` migrations for your storage engine. Automatic migration remains available; for controlled deployments, run the new image's `migrate` CLI once before starting gateway replicas with automatic migration disabled. See [migrations and upgrades](docs/operations.md#migrations-and-upgrades).
4. Review custom JSON catalogs against the new defaults. Add the aliases/mappings/prices for embeddings or Responses if needed. Existing single-key environment settings can remain; explicitly forward any new key-pool variables through a Compose service override.
5. Start the new gateway, check `/health/ready` and `/v1/models`, then verify existing keys, limits and application requests. Use the separate administrator bootstrap secret to create a local administrator through `/admin` if you want operator accounts.

Treat rollback as restoration of the pre-upgrade database backup together with the old pinned image and configuration. Do not assume the older binary supports the migrated schema. Restore/recovery commands and the validation limits are documented in the [operations guide](docs/operations.md).

### Verification

The release source is commit `1911fc881e39bbe033a7fa57a4d7ce9573a05c6c`. Its [successful source CI run](https://github.com/Mo7ammedd/LLMProxy/actions/runs/35991763024) ran 327 tests with PostgreSQL/Redis, a recovery drill and native AMD64/ARM64 container and SDK checks. Provider calls in automated checks use deterministic mocks; account access, live model capabilities and prices remain deployment-specific.

## [0.1.0] — 2026-09-24

- Initial OpenAI-compatible gateway with Chat Completions, SSE and function tools across OpenAI, Anthropic, Google Gemini and Azure OpenAI.
- Model aliases, priority/round-robin/random/fallback routing, bounded HTTP retries, gateway API keys, model permissions, RPM limits and lifetime token/USD allowances.
- SQLite standalone storage or PostgreSQL plus Redis, migrations, usage/cost accounting, quota recovery, health checks and telemetry.
- Docker/Compose deployment, GHCR publication, Python/TypeScript/C# SDK examples, and initial API, configuration, provider, architecture and operations documentation.

[0.2.0]: https://github.com/Mo7ammedd/LLMProxy/releases/tag/v0.2.0
[0.1.0]: https://github.com/Mo7ammedd/LLMProxy/releases/tag/v0.1.0
