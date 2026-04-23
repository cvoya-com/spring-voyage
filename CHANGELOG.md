# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versioning, release cadence, and breaking-change policy are documented in [`docs/developer/releases.md`](docs/developer/releases.md).

No versions have been tagged yet. The entries below capture the repository's history to date and will be finalised under the first tagged version when it is cut (see [#403](https://github.com/cvoya-com/spring-voyage/issues/403)). Entries reference GitHub pull requests and issues as the authoritative record.

## [Unreleased]

### Added

#### Open-source foundation

- Business Source License 1.1 (converts to Apache 2.0 on 2030-04-10), NOTICE, copyright headers, dependency audit, and community files ([#11](https://github.com/cvoya-com/spring-voyage/pull/11), [#14](https://github.com/cvoya-com/spring-voyage/pull/14), [#15](https://github.com/cvoya-com/spring-voyage/pull/15)).
- Contributor guide, issue templates, and cross-repo agent awareness ([21c72aa](https://github.com/cvoya-com/spring-voyage/commit/21c72aa)).
- CI workflow: build, test, format check, CodeQL, agent-definition lint, connector-web lint, web lint/build, Python lint/test, OpenAPI and Kiota drift checks, path-filtered jobs, merge-queue-aware gating ([af00993](https://github.com/cvoya-com/spring-voyage/commit/af00993), [2a9ea01](https://github.com/cvoya-com/spring-voyage/commit/2a9ea01), [#152](https://github.com/cvoya-com/spring-voyage/pull/152), [#154](https://github.com/cvoya-com/spring-voyage/pull/154), [#178](https://github.com/cvoya-com/spring-voyage/pull/178), [#190](https://github.com/cvoya-com/spring-voyage/pull/190), [#194](https://github.com/cvoya-com/spring-voyage/pull/194), [#304](https://github.com/cvoya-com/spring-voyage/pull/304)).
- Roadmap restructured into phased documents with OSS/Private feature split ([#40df5ba](https://github.com/cvoya-com/spring-voyage/commit/40df5ba), [#88](https://github.com/cvoya-com/spring-voyage/pull/88), [#419](https://github.com/cvoya-com/spring-voyage/pull/419)).
- Extensibility convention: `TryAdd*` DI, interface-first Core, no tenant assumptions ([#53](https://github.com/cvoya-com/spring-voyage/pull/53)).
- UI/CLI feature parity convention ([#323](https://github.com/cvoya-com/spring-voyage/pull/323)).
- Docs-with-feature convention ([#424](https://github.com/cvoya-com/spring-voyage/pull/424)).

#### Phase 1 — Platform foundation and software engineering domain (complete)

- .NET 10 host with Dapr virtual actors: `AgentActor`, `UnitActor`, `ConnectorActor`, `HumanActor` ([#501601c](https://github.com/cvoya-com/spring-voyage/commit/501601c), [#727](https://github.com/cvoya-com/spring-voyage/pull/727)).
- `IAddressable` / `IMessageReceiver` and message router with partitioned mailbox and conversation suspension ([b1da0d1](https://github.com/cvoya-com/spring-voyage/commit/b1da0d1), [#726](https://github.com/cvoya-com/spring-voyage/pull/726)).
- AI, Workflow, and Hybrid orchestration strategies ([#725](https://github.com/cvoya-com/spring-voyage/pull/725)).
- Four-layer prompt assembly (platform / unit context / conversation context / agent instructions) ([#724](https://github.com/cvoya-com/spring-voyage/pull/724)).
- Platform-internal Dapr Workflows for agent lifecycle and cloning ([#730](https://github.com/cvoya-com/spring-voyage/pull/730)).
- Delegated (container) execution dispatcher; hosted execution removed in favour of container delegation ([#722](https://github.com/cvoya-com/spring-voyage/pull/722), [#118](https://github.com/cvoya-com/spring-voyage/pull/118)).
- `checkMessages` and core set of platform tools for delegated agent message retrieval ([#728](https://github.com/cvoya-com/spring-voyage/pull/728)).
- GitHub connector (C#, Octokit), including DI registration ([#734](https://github.com/cvoya-com/spring-voyage/pull/734), [#95](https://github.com/cvoya-com/spring-voyage/pull/95)).
- API host with REST endpoints and single-user local-dev mode; OAuth login and API tokens ([#732](https://github.com/cvoya-com/spring-voyage/pull/732), [#736](https://github.com/cvoya-com/spring-voyage/pull/736)).
- `spring` CLI with core commands ([#735](https://github.com/cvoya-com/spring-voyage/pull/735)).
- PostgreSQL + EF Core + Dapr state store wrapper ([#731](https://github.com/cvoya-com/spring-voyage/pull/731), [#775](https://github.com/cvoya-com/spring-voyage/pull/775)).
- Software-engineering domain package (agent/unit templates, skills, workflow container) ([#753](https://github.com/cvoya-com/spring-voyage/pull/753)).
- Workflow-as-container deployment with Dapr sidecars ([#773](https://github.com/cvoya-com/spring-voyage/pull/773)).
- Phase 1 end-to-end integration tests ([#754](https://github.com/cvoya-com/spring-voyage/pull/754)).

#### Phase 2 — Observability and multi-human (complete)

- Enriched `ActivityEvent` model and Rx.NET event-bus pipeline ([#32](https://github.com/cvoya-com/spring-voyage/pull/32), [#47](https://github.com/cvoya-com/spring-voyage/pull/47), [#93](https://github.com/cvoya-com/spring-voyage/pull/93)).
- Streaming event types and Dapr pub/sub transport ([#35](https://github.com/cvoya-com/spring-voyage/pull/35)).
- Cost tracking service, aggregation, and budget enforcement ([#36](https://github.com/cvoya-com/spring-voyage/pull/36), [#48](https://github.com/cvoya-com/spring-voyage/pull/48), [#158](https://github.com/cvoya-com/spring-voyage/pull/158)).
- Multi-human RBAC with unit-scoped permissions ([#34](https://github.com/cvoya-com/spring-voyage/pull/34)).
- Clone state model, ephemeral lifecycle, clone API, and cost attribution ([#33](https://github.com/cvoya-com/spring-voyage/pull/33), [#37](https://github.com/cvoya-com/spring-voyage/pull/37), [#46](https://github.com/cvoya-com/spring-voyage/pull/46)).
- Real-time SSE endpoint and activity query API with Rx.NET push model ([#38](https://github.com/cvoya-com/spring-voyage/pull/38), [#40](https://github.com/cvoya-com/spring-voyage/pull/40)).
- React/Next.js web dashboard and portal ([#45](https://github.com/cvoya-com/spring-voyage/pull/45), [#388](https://github.com/cvoya-com/spring-voyage/pull/388)).

#### Phase 3 — Initiative and product-management domain (complete)

- Initiative types, policy model, and decision enums ([#92](https://github.com/cvoya-com/spring-voyage/pull/92)).
- `ICognitionProvider` interface with Tier-1 (Ollama) and Tier-2 (primary LLM) providers ([#94](https://github.com/cvoya-com/spring-voyage/pull/94), [#97](https://github.com/cvoya-com/spring-voyage/pull/97)).
- `IInitiativeEngine`, `ICancellationManager`, AgentActor integration, and API endpoints ([#97](https://github.com/cvoya-com/spring-voyage/pull/97)).
- Persisted initiative policies, budget tracker, and unit container handles in Dapr state ([#148](https://github.com/cvoya-com/spring-voyage/pull/148)).
- Product-management domain package (templates only; connector deferred) ([#139](https://github.com/cvoya-com/spring-voyage/pull/139)).
- Initiative dashboard page and initiative cost views ([#138](https://github.com/cvoya-com/spring-voyage/pull/138)).

#### Phase 4 — A2A, strategies, runtime, and portal UX (partial)

- A2A execution dispatcher replacing delegated execution; core model changes; CLI sidecar adapter ([#357](https://github.com/cvoya-com/spring-voyage/pull/357)).
- Codex and Gemini launchers as Tier-1 agent tools ([#358](https://github.com/cvoya-com/spring-voyage/pull/358)).
- Dapr Agent container with Ollama via A2A ([#360](https://github.com/cvoya-com/spring-voyage/pull/360)).
- Persistent agent hosting mode ([#361](https://github.com/cvoya-com/spring-voyage/pull/361)).
- Ollama as a first-class LLM backend for OSS and cloud deployments ([#333](https://github.com/cvoya-com/spring-voyage/pull/333)).
- Model- and provider-selection UX in CLI and UI ([#367](https://github.com/cvoya-com/spring-voyage/pull/367)).
- Dashboard: unit, agent, and activity detail views; card-based layout; activity timeline ([#378](https://github.com/cvoya-com/spring-voyage/pull/378), [#380](https://github.com/cvoya-com/spring-voyage/pull/380), [#384](https://github.com/cvoya-com/spring-voyage/pull/384), [#388](https://github.com/cvoya-com/spring-voyage/pull/388)).
- Activity log viewer (web portal) and `spring activity` CLI command ([#380](https://github.com/cvoya-com/spring-voyage/pull/380)).
- Delete-unit buttons in web portal (detail and list pages) ([#365](https://github.com/cvoya-com/spring-voyage/pull/365)).

#### Phase 5 — Unit nesting, directory, boundaries (partial)

- Recursive unit composition with cycle detection on add ([#220](https://github.com/cvoya-com/spring-voyage/pull/220)).
- M:N agent-to-unit membership with dispatch-time config overrides ([#245](https://github.com/cvoya-com/spring-voyage/pull/245), [#246](https://github.com/cvoya-com/spring-voyage/pull/246)).
- Directory service persisted to Postgres with write-through cache ([#382](https://github.com/cvoya-com/spring-voyage/pull/382)).
- Agents tab: add-agent dialog, per-membership edit, remove ([#329](https://github.com/cvoya-com/spring-voyage/pull/329)).
- Unified `IAgent` interface across agents and units ([#213](https://github.com/cvoya-com/spring-voyage/pull/213)).
- Unit-scheme members surfaced in portal UI and CLI ([#353](https://github.com/cvoya-com/spring-voyage/pull/353)).
- `spring directory list` + `spring directory show <slug>` CLI verbs mirror the portal's `/directory` surface; hit payload widened with the full owner chain + `projection/{slug}` paths so a multi-level projected entry surfaces every projecting ancestor ([#555](https://github.com/cvoya-com/spring-voyage/pull/555) — closes [#528](https://github.com/cvoya-com/spring-voyage/issues/528), [#553](https://github.com/cvoya-com/spring-voyage/issues/553)).

#### Phase 6 — Platform maturity (in progress)

- Research domain package (agent templates, research-team unit, and skill bundles; additional research connectors deferred to follow-ups) ([#417](https://github.com/cvoya-com/spring-voyage/issues/417)).
- Research-domain arxiv connector (read-only `searchLiterature` + `fetchAbstract` skills, no auth, no webhooks) ([#562](https://github.com/cvoya-com/spring-voyage/issues/562)).
- Research-domain web-search connector — pluggable `IWebSearchProvider` abstraction with a Brave Search default implementation; unit-scoped secret references for API keys; never logs plaintext ([#563](https://github.com/cvoya-com/spring-voyage/issues/563)).

#### Work beyond original phasing

- **Policy framework.** Unit-level policies for skill, model, cost, execution mode, and initiative ([#251](https://github.com/cvoya-com/spring-voyage/pull/251), [#279](https://github.com/cvoya-com/spring-voyage/pull/279)).
- **Runtime-loadable skill bundles** at the package level ([#255](https://github.com/cvoya-com/spring-voyage/pull/255)).
- **Reflection-action dispatch** and mid-flight supervisor amendments ([#272](https://github.com/cvoya-com/spring-voyage/pull/272)).
- **Secrets stack.** Unit-scoped secrets CRUD with tenant-aware abstractions, secret origin tracking, unit-to-tenant inheritance, AES-GCM at-rest encryption with per-tenant Dapr components, rotation primitives and audit-decorator hook shape, and multi-version coexistence ([#207](https://github.com/cvoya-com/spring-voyage/pull/207), [#212](https://github.com/cvoya-com/spring-voyage/pull/212), [#218](https://github.com/cvoya-com/spring-voyage/pull/218), [#236](https://github.com/cvoya-com/spring-voyage/pull/236), [#259](https://github.com/cvoya-com/spring-voyage/pull/259), [#278](https://github.com/cvoya-com/spring-voyage/pull/278)).
- **GitHub connector depth.** Generic `IConnectorType` abstraction; richer webhook dispatch and issue CRUD; PR and comment CRUD; review / installation webhooks, mention search, and label state machine; retry policy and rate-limit tracker; GraphQL client foundation and review-thread skills; connector lifecycle and topology (webhook CRUD, installations, token cache); response cache with webhook-driven invalidation; OAuth App auth surface; persisted rate-limit tracker and GraphQL batching; read-only and mutating Projects v2 integration ([#197](https://github.com/cvoya-com/spring-voyage/pull/197), [#238](https://github.com/cvoya-com/spring-voyage/pull/238), [#244](https://github.com/cvoya-com/spring-voyage/pull/244), [#252](https://github.com/cvoya-com/spring-voyage/pull/252), [#254](https://github.com/cvoya-com/spring-voyage/pull/254), [#264](https://github.com/cvoya-com/spring-voyage/pull/264), [#267](https://github.com/cvoya-com/spring-voyage/pull/267), [#277](https://github.com/cvoya-com/spring-voyage/pull/277), [#288](https://github.com/cvoya-com/spring-voyage/pull/288), [#291](https://github.com/cvoya-com/spring-voyage/pull/291), [#292](https://github.com/cvoya-com/spring-voyage/pull/292), [#298](https://github.com/cvoya-com/spring-voyage/pull/298), [#299](https://github.com/cvoya-com/spring-voyage/pull/299)).
- **Unit lifecycle and CRUD.** Unit creation wizard (multi-step, template imports, connector binding); agent-detail route with clone management and cost/budget views; `spring apply` CLI; lifecycle-aware start/stop with compound Draft→Starting and readiness checks ([#113](https://github.com/cvoya-com/spring-voyage/pull/113), [#130](https://github.com/cvoya-com/spring-voyage/pull/130), [#138](https://github.com/cvoya-com/spring-voyage/pull/138), [#146](https://github.com/cvoya-com/spring-voyage/pull/146), [#149](https://github.com/cvoya-com/spring-voyage/pull/149), [#335](https://github.com/cvoya-com/spring-voyage/pull/335), [#369](https://github.com/cvoya-com/spring-voyage/pull/369)).
- **Multi-AI agent runtime.** Claude Code, Codex, Gemini, Ollama, Dapr Agents, and custom A2A agents supported as execution targets ([#333](https://github.com/cvoya-com/spring-voyage/pull/333), [#346–361 cluster: #357](https://github.com/cvoya-com/spring-voyage/pull/357), [#358](https://github.com/cvoya-com/spring-voyage/pull/358), [#360](https://github.com/cvoya-com/spring-voyage/pull/360), [#361](https://github.com/cvoya-com/spring-voyage/pull/361)).
- **E2E test harness.** Shell-based CLI scenarios against a live local stack; nested-units scenario; cascading cleanup; fast/LLM split; unique run ids with `--sweep` orphan cleanup ([#313](https://github.com/cvoya-com/spring-voyage/pull/313), [#317](https://github.com/cvoya-com/spring-voyage/pull/317), [#327](https://github.com/cvoya-com/spring-voyage/pull/327), [#332](https://github.com/cvoya-com/spring-voyage/pull/332), [#343](https://github.com/cvoya-com/spring-voyage/pull/343)).
- **OpenAPI-first API surface.** .NET 10 native OpenAPI with build-time emission; named response records; OpenAPI drift CI; web migrated to generated types and `openapi-fetch`; CLI migrated to typed Kiota client with Kiota drift CI ([#169](https://github.com/cvoya-com/spring-voyage/pull/169), [#177](https://github.com/cvoya-com/spring-voyage/pull/177), [#178](https://github.com/cvoya-com/spring-voyage/pull/178), [#179](https://github.com/cvoya-com/spring-voyage/pull/179), [#182](https://github.com/cvoya-com/spring-voyage/pull/182), [#184](https://github.com/cvoya-com/spring-voyage/pull/184), [#187](https://github.com/cvoya-com/spring-voyage/pull/187), [#189](https://github.com/cvoya-com/spring-voyage/pull/189)).
- **Deployment.** Dapr production component configs with local/prod profile split; Caddy multi-host template and webhook relay tunnel; VPS Podman deployment scripts; per-app Dapr sidecars in Podman deployment; DataProtection keys persisted across rebuilds; standalone Next.js build ([#140](https://github.com/cvoya-com/spring-voyage/pull/140), [#143](https://github.com/cvoya-com/spring-voyage/pull/143), [#144](https://github.com/cvoya-com/spring-voyage/pull/144), [#257](https://github.com/cvoya-com/spring-voyage/pull/257), [#309](https://github.com/cvoya-com/spring-voyage/pull/309), [#342](https://github.com/cvoya-com/spring-voyage/pull/342)).
- **Connector web UI hosting.** Connector web submodules hosted inside each connector package with CI validation ([#214](https://github.com/cvoya-com/spring-voyage/pull/214)).
- **CLI/API parity.** `spring agent`, `spring membership`, cascading purge, unit create flags, `from-template`, unit-as-member, HttpClient consolidated into a shared `ClientFactory` ([#326](https://github.com/cvoya-com/spring-voyage/pull/326), [#335](https://github.com/cvoya-com/spring-voyage/pull/335), [#354](https://github.com/cvoya-com/spring-voyage/pull/354), [#356](https://github.com/cvoya-com/spring-voyage/pull/356)).
- **Skills tab** for per-agent skill configuration ([#165](https://github.com/cvoya-com/spring-voyage/pull/165)).
- **Foundation documentation refresh.** Architecture docs updated for shipped A2A / policy / secrets features; docs-with-feature convention ([#424](https://github.com/cvoya-com/spring-voyage/pull/424), [#425](https://github.com/cvoya-com/spring-voyage/pull/425)).

### Changed

- Migrated test assertions from FluentAssertions to Shouldly ([#157](https://github.com/cvoya-com/spring-voyage/pull/157)).
- All 4xx endpoint responses now return `ProblemDetails` ([#192](https://github.com/cvoya-com/spring-voyage/pull/192)).
- `JsonStringEnumConverter` registered globally for API enum serialization ([#153](https://github.com/cvoya-com/spring-voyage/pull/153)).
- Agent-creation flow: creator granted Owner on unit creation; `MessageRouter` skipped for member adds ([#328](https://github.com/cvoya-com/spring-voyage/pull/328)).
- Worker is the single owner of EF Core migrations ([#318](https://github.com/cvoya-com/spring-voyage/pull/318)).
- EF Core migrations adopted for the Dapr `DbContext` ([#237](https://github.com/cvoya-com/spring-voyage/pull/237)).
- Roadmap restructured to reflect actual completion status and introduce issue-tracked planning ([#419](https://github.com/cvoya-com/spring-voyage/pull/419)).

### Removed

- Legacy `v`-prefixed OCI tag (`ghcr.io/cvoya-com/agent-base:vX.Y.Z`) from the agent-base release workflow; the unprefixed `:X.Y.Z` and `:latest` tags continue to be published ([#1121](https://github.com/cvoya-com/spring-voyage/issues/1121)).
- Hosted (in-process) execution path; all agentic work now delegated to containers ([#118](https://github.com/cvoya-com/spring-voyage/pull/118)).
- Container-launch responsibilities removed from unit start/stop API endpoints ([#373](https://github.com/cvoya-com/spring-voyage/pull/373)).
- PostgreSQL statestore component removed from local dev in favour of the Dapr state store wrapper ([70d6565](https://github.com/cvoya-com/spring-voyage/commit/70d6565)).

### Fixed

- `UnitMembershipBackfillService` no longer crashes the host when the Dapr sidecar isn't ready on startup ([#387](https://github.com/cvoya-com/spring-voyage/pull/387)).
- `UnitActor` orchestration resolution ([#313](https://github.com/cvoya-com/spring-voyage/pull/313)).
- Actor-proxy type name and Dapr control-plane flags ([#310](https://github.com/cvoya-com/spring-voyage/pull/310)).
- `DataContract` serialization across the Dapr actor-remoting boundary ([#322](https://github.com/cvoya-com/spring-voyage/pull/322)).
- `Tier1Options` positional-record shape breaking actor activation ([#341](https://github.com/cvoya-com/spring-voyage/pull/341)).
- Hardcoded `human://api` identity on read endpoints ([#344](https://github.com/cvoya-com/spring-voyage/pull/344)).
- `unit_memberships` populated on template-created units ([#345](https://github.com/cvoya-com/spring-voyage/pull/345)).
- Template-created agents auto-registered as directory entries ([#379](https://github.com/cvoya-com/spring-voyage/pull/379)).
- `__EFMigrationsHistory` pinned to the `spring` schema to fix migrator crash on existing databases ([#366](https://github.com/cvoya-com/spring-voyage/pull/366)).
- Unit-creation wizard: model dropdowns, template auth, scratch-create path ([#293](https://github.com/cvoya-com/spring-voyage/pull/293)).
- MCP server `StopAsync` tolerant of disposed `CancellationTokenSource` ([#141](https://github.com/cvoya-com/spring-voyage/pull/141)).
- Fail fast on missing `SpringDb` connection string ([#301](https://github.com/cvoya-com/spring-voyage/pull/301)).
- Sanitize `eventType` on invalid-signature webhook log ([#300](https://github.com/cvoya-com/spring-voyage/pull/300)).
- Host-time infrastructure gating during build-time OpenAPI generation ([#372](https://github.com/cvoya-com/spring-voyage/pull/372)).
- `?force=true` escape hatch for stuck unit deletes ([#156](https://github.com/cvoya-com/spring-voyage/pull/156)).
- `deploy.sh` unbound-variable crash when `OLLAMA_GPU` is unset ([#336](https://github.com/cvoya-com/spring-voyage/pull/336)).
- Softened missing-tool skill-bundle validation to a warning ([#307](https://github.com/cvoya-com/spring-voyage/pull/307)).

### Security

- BSL 1.1 licensing and copyright headers across the codebase ([#11](https://github.com/cvoya-com/spring-voyage/pull/11)).
- CodeQL C# analysis wired into pull-request, merge-queue, and scheduled runs ([#152](https://github.com/cvoya-com/spring-voyage/pull/152), [#194](https://github.com/cvoya-com/spring-voyage/pull/194)).
- AES-GCM at-rest encryption for OSS secret store with per-tenant Dapr components ([#236](https://github.com/cvoya-com/spring-voyage/pull/236)).
