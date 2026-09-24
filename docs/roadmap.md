# Roadmap

The gateway expansion and six provider/release operations improvements are implemented in version 0.3. See the [management and extended API guide](expanded-api.md), [configuration reference](configuration.md) and [implementation checklist](implementation-plan.md).

| Area | Implemented |
| --- | --- |
| Capability routing | Adapter/model flags, feature-combination and context checks before strategy selection and quota reservation |
| Operator access and key lifecycle | Local administrator/operator/auditor roles, expiring sessions, management/rejection audit, key expiry and rotation with grace |
| Billing and quotas | Atomic lifetime and UTC monthly allowances, cached/tier prices, individual upstream attempts and idempotent invoice adjustments |
| Reporting | Cursor pages, owner/date/model/provider/status filters, database rollups, CSV, embedded dashboard and retention |
| Configuration | Named accounts, encrypted dashboard-managed keys, shared Redis rotation/cooldowns, per-key statistics, optional live access checks and validated reload |
| Alerts | Durable budget/error/latency/pool incidents, acknowledgment, recovery and optional leased webhook delivery |
| Releases and repository | Signed images, scan attestations, SPDX SBOMs/provenance, vulnerability gates, pinned build inputs, protected main and enabled security reporting/scanning |
| Routing and concurrency | Six strategies including cost/latency, plus shared global/key/model/provider concurrency leases |
| Extended protocols | Embeddings, native stateless Responses, image/audio input, reasoning controls and durable files/batches |
| Operational validation | Load/long-stream probes, two-instance recovery and Redis-loss drill, PostgreSQL and SQLite restore checks, native ARM64 container/SDK CI job |

## Follow-up work

| Area | Remaining scope |
| --- | --- |
| Enterprise identity | OIDC/SSO, MFA and owner-scoped operator permissions. Current roles are local and apply gateway-wide. |
| Provider metadata | Maintained model capability/pricing feeds and scheduled live checks. Manual credential/model probes are available with explicit opt-in; prices remain operator-supplied estimates. |
| Billing integrations | Provider-specific invoice importers and automatic matching. Current reconciliation accepts explicit attempt IDs, actual costs and references. Unknown retry/crash charges require investigation. |
| Responses state | Per-key ownership for stored responses, conversations, background work, hosted tools and provider files. The current Responses API enforces stateless requests. |
| Provider-native batches | Native batch APIs/discounts and scheduled retry queues. Current durable batches run ordinary requests and preserve uncertain interrupted outcomes. |
| Additional protocols | Image generation, speech generation/transcription, video, log probabilities, Foundry Agents/projects and the separate Anthropic-on-Foundry API. |
| Shared routing observations | Distributed latency measurements, workload-specific performance targets and broader partition/soak testing. Current latency EWMA is local to each instance. |
| Optional packages | Versioned NuGet distribution if integrators need it. Deployment uses the standalone gateway image. |

## Validation and distribution

Automated provider tests and SDK smoke runs use deterministic mocks. They do not establish compatibility with every live model, account, region or provider-specific parameter combination. The recovery drill records local measurements in `artifacts/recovery-drill.json`; production capacity requires representative workloads.

The [successful 0.2.0 source run](https://github.com/Mo7ammedd/LLMProxy/actions/runs/35991763024) passed unit/provider/database tests, the recovery drill and native AMD64/ARM64 container checks. Each new publication must pass these checks again. Current CI publishes to Docker Hub and GHCR, verifies the image digest/platforms in both registries and synchronizes the Docker Hub overview after main builds. See the [release guide](releases.md) for credentials, tag policy and the original 0.2.0 image provenance.

Dependabot is configured for weekly NuGet, Docker, GitHub Actions, npm and Python dependency updates. Every new publication verifies signatures, emits SBOMs/provenance and enforces its documented vulnerability policy. See [provider operations](provider-operations.md) for the dashboard and alerting model.
