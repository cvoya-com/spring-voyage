# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). The **[Unreleased]** section is generated from Conventional Commits by [git-cliff](https://git-cliff.org) — run `eng/release/update-changelog.sh`; do not hand-edit it. Earlier history below the marker is hand-curated and frozen.

## [Unreleased]

### Features

- **deploy:** Add spring-debug-dump.sh diagnostic collector
- **docs:** Add incident report for Magazine Team Edition 1
- **deploy:** Decode actor inboxes in spring-debug-dump.sh ([#2983](https://github.com/cvoya-com/spring-voyage/pull/2983))
- **magazine:** Explicit decision-authority chain + team decision-advertising ([#3007](https://github.com/cvoya-com/spring-voyage/pull/3007))
- **tools:** Single source of truth for the sv.* category catalog ([#3011](https://github.com/cvoya-com/spring-voyage/pull/3011))
- **memory:** Structured JSON content in sv.memory.* (jsonb) ([#3019](https://github.com/cvoya-com/spring-voyage/pull/3019))
- **memory:** Add sv.memory.get_messages + durable-memory & identity contract clauses ([#3022](https://github.com/cvoya-com/spring-voyage/pull/3022))
- **memory:** Typed object/text variants + participant-set scope for sv.memory.* (#3038, #3037) ([#3043](https://github.com/cvoya-com/spring-voyage/pull/3043))
- **messaging:** Gate tenant-user→unit/agent sends on Hat reachability + GC orphaned Hats ([#2972](https://github.com/cvoya-com/spring-voyage/pull/2972)) ([#3046](https://github.com/cvoya-com/spring-voyage/pull/3046))
- **messaging:** Deliver a thread's pending inbox as one batched runtime turn ([#3056](https://github.com/cvoya-com/spring-voyage/pull/3056)) ([#3058](https://github.com/cvoya-com/spring-voyage/pull/3058))
- **costs,runtime:** Capture Claude CLI cost end-to-end; daily + unit budget enforcement ([#3074](https://github.com/cvoya-com/spring-voyage/pull/3074))
- **runtime,magazine:** A2a-process runtime + LangGraph magazine orchestrator ([#3072](https://github.com/cvoya-com/spring-voyage/pull/3072))
- **runtime,magazine:** Claude manages the LangGraph engine via tools; a2a-process OAuth ([#3077](https://github.com/cvoya-com/spring-voyage/pull/3077))
- **release:** Generate CHANGELOG.md + per-release Release-body delta (git-cliff) ([#3095](https://github.com/cvoya-com/spring-voyage/pull/3095))
- **costs:** Native/SDK-runtime cost, clone roll-up, initiative classification, dead-code cleanup ([#3075](https://github.com/cvoya-com/spring-voyage/pull/3075)) ([#3110](https://github.com/cvoya-com/spring-voyage/pull/3110))
- **orchestrator:** Opt-in graph-derivable command registry for the localhost MCP tool surface ([#3078](https://github.com/cvoya-com/spring-voyage/pull/3078)) ([#3114](https://github.com/cvoya-com/spring-voyage/pull/3114))
- **config:** Runtime/DB config as the single source of truth — reconstruct export, decide single-home model/hosting ([#3090](https://github.com/cvoya-com/spring-voyage/pull/3090)) ([#3113](https://github.com/cvoya-com/spring-voyage/pull/3113))
- **deploy:** TLS_MODE — HTTPS without internet-facing (Caddy internal CA / custom cert / ACME) ([#3118](https://github.com/cvoya-com/spring-voyage/pull/3118))
- **costs:** Attribute SDK turn cost to owning unit; drop orphaned ICostTracker ([#3121](https://github.com/cvoya-com/spring-voyage/pull/3121))
- **codex,sidecar,activity:** Wire Codex MCP via config.toml, parse Codex JSONL, ingest stream events as activities ([#3140](https://github.com/cvoya-com/spring-voyage/pull/3140))

### Bug fixes

- **release:** Let a pre-release opt into :latest (images + GitHub badge) ([#2971](https://github.com/cvoya-com/spring-voyage/pull/2971))
- **packages,web:** Allow binding a connector a package doesn't declare ([#2973](https://github.com/cvoya-com/spring-voyage/pull/2973))
- **web:** Expand/collapse branch nodes when clicking their label in the explorer ([#2974](https://github.com/cvoya-com/spring-voyage/pull/2974))
- **messaging:** Remove the per-thread hop counter that killed long conversations ([#2976](https://github.com/cvoya-com/spring-voyage/pull/2976))
- **cli:** Single-quote env-file values so `source spring.env` doesn't run "RSA" ([#3010](https://github.com/cvoya-com/spring-voyage/pull/3010))
- **execution:** Preserve persistent-agent workspace volume across restarts & resumable stops ([#2999](https://github.com/cvoya-com/spring-voyage/pull/2999)) ([#3012](https://github.com/cvoya-com/spring-voyage/pull/3012))
- **sidecar:** Isolate per-turn MCP token per thread to stop concurrent-thread 401s ([#3000](https://github.com/cvoya-com/spring-voyage/pull/3000)) ([#3017](https://github.com/cvoya-com/spring-voyage/pull/3017))
- **messaging:** Bound sv.messaging delivery with a per-attempt timeout ([#3004](https://github.com/cvoya-com/spring-voyage/pull/3004)) ([#3014](https://github.com/cvoya-com/spring-voyage/pull/3014))
- **web:** Unblock web-typecheck on main and refresh tools-panel fixture ([#3021](https://github.com/cvoya-com/spring-voyage/pull/3021))
- **execution:** Disable Claude Code native auto-memory ([#2985](https://github.com/cvoya-com/spring-voyage/pull/2985)) ([#3023](https://github.com/cvoya-com/spring-voyage/pull/3023))
- **hosting:** Runtime polish — unit-issues 400, PK_messages flood, SSE cutoff, Draft-on-cancel ([#3025](https://github.com/cvoya-com/spring-voyage/pull/3025))
- **execution:** Lifecycle machinery hardening — introspection retry, volume GC, restart semantics (#3002, #3003, #3005) ([#3026](https://github.com/cvoya-com/spring-voyage/pull/3026))
- **web:** Patch 3 Dependabot npm advisories (postcss, vitest, brace-expansion) ([#3027](https://github.com/cvoya-com/spring-voyage/pull/3027))
- **execution:** Make unit/agent stop authoritative — lifecycle mirror + message-path gates ([#2981](https://github.com/cvoya-com/spring-voyage/pull/2981)) ([#3028](https://github.com/cvoya-com/spring-voyage/pull/3028))
- **prompt:** Enumerate + promote the full sv.memory.* surface in the platform prompt ([#3030](https://github.com/cvoya-com/spring-voyage/pull/3030))
- **execution:** Give UnitActor AgentActor's per-thread mailbox + drain (busy unit no longer fails inbound delivery) ([#3033](https://github.com/cvoya-com/spring-voyage/pull/3033))
- **skills:** Sv.* tool-contract hardening — reject empty messages, retry-guiding required-arg errors (#3035, #3036) ([#3040](https://github.com/cvoya-com/spring-voyage/pull/3040))
- **deploy:** Reset.sh platform level now removes agent workspace volumes (spring-ws-*) ([#3049](https://github.com/cvoya-com/spring-voyage/pull/3049))
- **release:** Fix --latest not marking pre-release as latest ([#3051](https://github.com/cvoya-com/spring-voyage/pull/3051))
- **release:** Build agent runtime images multi-arch (arm64) ([#3054](https://github.com/cvoya-com/spring-voyage/pull/3054))
- **magazine:** Make the Managing Editor the pipeline routing authority ([#3057](https://github.com/cvoya-com/spring-voyage/pull/3057))
- **magazine,runtime:** Coordination prompt + memory/thread/directory fixes (A+B+C) ([#3068](https://github.com/cvoya-com/spring-voyage/pull/3068))
- **runtime,docs:** Keep internal thread_id off the model-visible surface ([#3079](https://github.com/cvoya-com/spring-voyage/pull/3079)) ([#3082](https://github.com/cvoya-com/spring-voyage/pull/3082))
- **magazine-langgraph:** Make orchestrator image dispatch-ready ([#3084](https://github.com/cvoya-com/spring-voyage/pull/3084))
- **install:** Point the spring CLI at the deployed API endpoint ([#3093](https://github.com/cvoya-com/spring-voyage/pull/3093))
- **cli,install:** Reconcile the spring CLI endpoint on re-install ([#3094](https://github.com/cvoya-com/spring-voyage/pull/3094))
- **magazine,directory,messaging:** Make magazine-langgraph editions run and fail loudly ([#3088](https://github.com/cvoya-com/spring-voyage/pull/3088))
- **cloud-env:** Make Dapr opt-in so default setup succeeds under Trusted ([#3098](https://github.com/cvoya-com/spring-voyage/pull/3098))
- **api:** Return 404 not 500 for PostThreadMessage on unknown thread id ([#3087](https://github.com/cvoya-com/spring-voyage/pull/3087)) ([#3103](https://github.com/cvoya-com/spring-voyage/pull/3103))
- **runtime:** Harden native-A2A launch — specific failures over silent readiness timeout ([#3085](https://github.com/cvoya-com/spring-voyage/pull/3085)) ([#3109](https://github.com/cvoya-com/spring-voyage/pull/3109))
- **runtime:** Default BYOI container CWD to image WORKDIR; CLI launchers set workspace-CWD explicitly ([#3106](https://github.com/cvoya-com/spring-voyage/pull/3106)) ([#3115](https://github.com/cvoya-com/spring-voyage/pull/3115))
- Unblock local single-host agent turns — resolve components path + fail fast on missing bind-mount source ([#3101](https://github.com/cvoya-com/spring-voyage/pull/3101)) ([#3116](https://github.com/cvoya-com/spring-voyage/pull/3116))
- **install,deploy:** Harden install/uninstall + smoke-test rootless port floor ([#3120](https://github.com/cvoya-com/spring-voyage/pull/3120))
- **runtime,sidecar:** Make Codex dispatchable, fix false catalogue claims, parse stream-json (#2118/#2119/#3018/#2226) ([#3126](https://github.com/cvoya-com/spring-voyage/pull/3126))
- **release:** Honor --latest on pre-releases; make changelog check advisory ([#3145](https://github.com/cvoya-com/spring-voyage/pull/3145))

### Refactor

- **skills:** Unify tool execution-context on the caller-aware path; remove sv.expertise.* ([#2989](https://github.com/cvoya-com/spring-voyage/pull/2989)) ([#3009](https://github.com/cvoya-com/spring-voyage/pull/3009))
- **memory:** Rename kind→scope axis; derive from thread_id ([#2997](https://github.com/cvoya-com/spring-voyage/pull/2997)) ([#3015](https://github.com/cvoya-com/spring-voyage/pull/3015))
- **runtime:** Remove agent-facing "thread" semantics (#3041 Parts B & C + config rename) ([#3044](https://github.com/cvoya-com/spring-voyage/pull/3044))
- **directory:** Consolidate unit member + role resolution into one DB-backed seam ([#3089](https://github.com/cvoya-com/spring-voyage/pull/3089)) ([#3105](https://github.com/cvoya-com/spring-voyage/pull/3105))
- **directory:** Consolidate sv.directory.* to a minimal, non-overlapping surface ([#3069](https://github.com/cvoya-com/spring-voyage/pull/3069)) ([#3127](https://github.com/cvoya-com/spring-voyage/pull/3127))
- **directory:** Resolve agent/unit type from the DB/cache, not the address scheme ([#2084](https://github.com/cvoya-com/spring-voyage/pull/2084)) ([#3136](https://github.com/cvoya-com/spring-voyage/pull/3136))
- **directory,routing:** Resolve kind from the DB/cache seam, not the address scheme (#3131, #3133, #3134); membership mismatch is not-found ([#3132](https://github.com/cvoya-com/spring-voyage/pull/3132)) ([#3139](https://github.com/cvoya-com/spring-voyage/pull/3139))

### Documentation

- **adr:** ADR-0065 — agent memory architecture ([#2995](https://github.com/cvoya-com/spring-voyage/pull/2995))
- **adr-0065:** Amend — typed memory tool variants (object-primary) + thread-scope-unused note ([#3039](https://github.com/cvoya-com/spring-voyage/pull/3039))
- Simplify root README and sync the canonical container-image list ([#3070](https://github.com/cvoya-com/spring-voyage/pull/3070))
- Update development setup prerequisites for clarity and specificity
- Align README host prerequisites with setup.md

### Other

- Single home for unit/agent model & hosting — remove the definition-jsonb dual-home + dispatch overlay (ADR-0067) ([#3117](https://github.com/cvoya-com/spring-voyage/pull/3117))

<!-- BEGIN FROZEN HISTORY -->

## Earlier history (hand-curated)

> These entries predate the switch to a generated changelog. The **[Unreleased]**
> section above is generated from Conventional Commits by git-cliff
> (`eng/release/update-changelog.sh`); everything below this point is frozen.

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

#### Operations & deployment

- **`deployment/` reorganised into `devops/{build,deploy,install}/`** — source-build artefacts (Dockerfiles, `build*.sh`, agent-extension examples) moved to `devops/build/`; operator-facing scripts and runtime config (`deploy.sh`, `setup.sh`, `spring-voyage-host.sh`, `Caddyfile*`, `docker-compose.yml`, `spring.env.example`, `scripts/`) moved to `devops/deploy/`; `devops/install/` reserved for the source-free installer (#2180). The TypeScript agent sidecar moved from `deployment/agent-sidecar/` to `src/Cvoya.Spring.AgentSidecar/` so it joins the existing `src/Cvoya.Spring.<Component>/` convention. Pure rename + reference rewrite — no functional changes. ([#2171](https://github.com/cvoya-com/spring-voyage/pull/2171)).
- **`deploy.sh init` subcommand** — first-run bootstrap that copies `devops/deploy/spring.env.example` to `devops/deploy/spring.env` (creating it if missing) and provisions a freshly-generated `SPRING_SECRETS_AES_KEY` in one step. Refuses to overwrite an existing non-placeholder key — rotating it would orphan every encrypted secret in the state store, so rotation is documented as an explicit follow-up (see `docs/developer/secret-store.md`). Pairs with the secrets-key removal of `Secrets:AllowEphemeralDevKey`: `deploy.sh up` now validates a non-placeholder key at start time and points operators at `init` when missing, instead of failing later with a cryptic encryptor exception.
- **Release pipeline publishes platform image, deployment bundle, and `spring` CLI binaries on every `v*.*.*` tag** — `.github/workflows/release.yml` now builds `ghcr.io/cvoya-com/spring-voyage:{<version>, <major.minor>, latest}` from `devops/build/Dockerfile`, packages the operator-facing slice of `devops/deploy/` plus `dapr/components/delegated-spring-voyage-agent/` and a `manifest.json` into `spring-voyage-<v>-bundle.tar.gz`, publishes the `spring` CLI for the 5-RID matrix (linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64), and emits a `SHA256SUMS` covering every release asset. The dispatcher self-contained build is now part of the same tag flow — `.github/workflows/release-spring-dispatcher.yml` was deleted and its 5-RID matrix moved into a `publish-dispatcher` job in `release.yml`, so the platform image, deployment bundle, dispatcher binary, and `spring` CLI all share one release tag instead of the prior `dispatcher-v*` / `v*` split ([#2172](https://github.com/cvoya-com/spring-voyage/issues/2172)).
- **Source-free local-host installer (`devops/install/install.sh`)** — canonical operator entry-point. `curl -fSL …/install.sh | bash` validates pre-flight (Podman 4+, ports 80/443, `~/.local/bin` on PATH, `podman machine` running on macOS), resolves the release tag, downloads the deployment bundle + dispatcher binary + `spring` CLI, verifies them against `SHA256SUMS`, pulls the platform image, generates `~/.spring-voyage/spring.env` (mode 0600), starts the stack, and optionally drives the GitHub-App manifest flow via the existing `spring github-app register`. Two prompts only: `DEPLOY_HOSTNAME` (default `localhost`) and the GitHub-App opt-in; `--yes` skips both ([#2180](https://github.com/cvoya-com/spring-voyage/issues/2180)).
- **First-class uninstall (`devops/install/uninstall.sh`)** — `voyage uninstall` removes containers/volumes/networks/images and the install-root release assets while preserving `spring.env`, `~/.spring-voyage/host/`, and `~/.spring-voyage/workspaces/`. `--purge` is a factory reset that removes those too. Ships inside the deployment bundle so it survives install-root cleanup mid-teardown ([#2180](https://github.com/cvoya-com/spring-voyage/issues/2180)).
- **ADR-0042 — Local-host operator installer** — records the eight installer decisions (maximally automate, curlable `install.sh` is the canonical entry-point, Podman-first local-host only, native CLI binaries on the 5-RID matrix, no upgrades in v0.1, first-class uninstall, local-clone unchanged, single release-version surface). Cross-references ADR-0012 (dispatcher service extraction), ADR-0028 (tenant topology), and [#1063](https://github.com/cvoya-com/spring-voyage/issues/1063) ([#2180](https://github.com/cvoya-com/spring-voyage/issues/2180)).
- **`voyage` operator wrapper gains `status`, `logs`, `restart`, `version`** — the installer-shipped wrapper at `~/.local/bin/voyage` now reports install version + container/dispatcher health + web URL + log paths (`status`), tails container or host-dispatcher logs (`logs [service]`, `logs dispatcher`), restarts the stack (`restart`), and prints the manifest version and platform image (`version`). The wrapper is factored out of the inline heredoc in `install.sh` into a checked-in, shellcheck-clean script at `devops/install/voyage`, shipped in the deployment bundle ([#2173](https://github.com/cvoya-com/spring-voyage/issues/2173)).

### Changed

- **Release notes sourced from checked-in `docs/releases/` files.** The GitHub Release body is now resolved by [`eng/release/resolve-release-notes.sh`](eng/release/resolve-release-notes.sh): a curated `docs/releases/<version>.md` (release-line file, e.g. `1.0.0.md`) when present, otherwise the `CHANGELOG.md` `[Unreleased]` section as before. Adds the `/release-notes` authoring command, `docs/releases/` (with the first-public-release notes), and a § Release notes in [`docs/developer/releases.md`](docs/developer/releases.md#release-notes). The changelog remains the per-PR technical record; the release notes are the human narrative for the Releases page ([#2922](https://github.com/cvoya-com/spring-voyage/pull/2922)).
- **Operator-facing release assets collapsed into one archive per RID.** The previous three separate operator-facing assets (`spring-voyage-<v>-bundle.tar.gz`, `spring-voyage-dispatcher-<v>-<rid>.{tar.gz,zip}`, `spring-<v>-<rid>.{tar.gz,zip}`) are gone. Every release now publishes one `spring-voyage-<v>-<rid>.{tar.gz,zip}` per RID containing `bundle/`, `cli/`, and `dispatcher/` subdirectories. The installer downloads one file instead of three, and `SHA256SUMS` shrinks proportionally. The bundle is no longer a directly-downloadable asset — it ships as a subdirectory inside every per-RID archive. Sidecar SEA binaries (BYOI path 2) are unchanged: still attached as three top-level release files for image extenders who `curl … && chmod +x` them into a custom Dockerfile.
- **`install-<v>.sh` version-baked installer.** Every release now attaches a companion installer next to the unversioned `install.sh`. It is byte-identical except that its `BAKED_VERSION` is filled in at release time, so the script refuses to install any other version (`--version` or `$SPRING_VOYAGE_VERSION` pointing elsewhere fails loud). Useful for documentation, runbooks, or CI that need to pin to a specific release without depending on `--version` or `releases/latest`. The unversioned `install.sh` remains the canonical `curl … releases/latest/download/install.sh | bash` entry point.
- **`install.sh` tag-handling fix.** The unversioned installer has been silently broken against every release since [#2229](https://github.com/cvoya-com/spring-voyage/issues/2229) renamed the release tag to the `spring-voyage-v*` form: the script's version-normalisation block assumed the old `v*` tag shape and built `releases/download/vspring-voyage-v.../...` URLs that 404'd. The new resolver accepts `1.0.0`, `v1.0.0`, or `spring-voyage-v1.0.0` as version input and normalises consistently to `TAG=spring-voyage-v${SEMVER}`.
- **GHCR visibility-PATCH steps removed; visibility now inherits from the source repo.** The `Make GHCR package public` steps in every image-publishing job were calling `PATCH /orgs/{org}/packages/{package_type}/{package_name}` — a REST endpoint that does not exist (org packages only support GET / DELETE / `.../restore`). The step has been silently failing since its inception (initially masked by `continue-on-error: true`, then loud after [#2229](https://github.com/cvoya-com/spring-voyage/issues/2229) dropped that flag, then dressed up as a propagation race by [#2240](https://github.com/cvoya-com/spring-voyage/issues/2240)'s retry-on-404 wrapper). Packages were ending up public anyway via the `cvoya-com` org's "Inherit access from source repository" setting combined with the `org.opencontainers.image.source` OCI label our images carry. Deleted all four occurrences plus the `devops/release/make-package-public.sh` helper; replaced with explanatory comments.
- **NuGet publish switched to OIDC trusted publishing.** `publish-spring-cli-nuget` no longer reads `NUGET_API_KEY`. Uses `NuGet/login@v1` to exchange the GitHub workflow's OIDC token for a short-lived NuGet API key per release. Configuration moves from a secret to a `NUGET_USER` repository **variable** (the nuget.org account username — not sensitive). The job skips cleanly when `NUGET_USER` is unset so forks without a NuGet account can still run `release.yml`. Trusted-publisher entry on the cvoya-com NuGet account is the load-bearing config; see [`docs/developer/releases.md`](docs/developer/releases.md#nuget-package-publishing) for the setup.

- **Release pipeline unified under a single `spring-voyage-v*` tag (Path B; supersedes the partial fixes in [77cab767](https://github.com/cvoya-com/spring-voyage/commit/77cab767), [7a94f644](https://github.com/cvoya-com/spring-voyage/commit/7a94f644), [c6e82e28](https://github.com/cvoya-com/spring-voyage/commit/c6e82e28), [65fe267f](https://github.com/cvoya-com/spring-voyage/commit/65fe267f), [bf43e0c3](https://github.com/cvoya-com/spring-voyage/commit/bf43e0c3), [8a5258c5](https://github.com/cvoya-com/spring-voyage/commit/8a5258c5), [4fbfe79a](https://github.com/cvoya-com/spring-voyage/commit/4fbfe79a), [1d6e36e4](https://github.com/cvoya-com/spring-voyage/commit/1d6e36e4)).** The previous three-tag chain (`agent-base-v*` → `oss-agents-v*` → `v*`) and the two auxiliary workflows (`release-agent-base.yml`, `release-oss-agent-images.yml`) are collapsed into a single `spring-voyage-v<version>` tag triggering the unified `release.yml`. `devops/release/release.sh` is now a thin wrapper that pushes one tag and watches one workflow run. Two real bugs are fixed in the same PR: (1) the buggy single-arch `Push spring-voyage-agent-base` step in the previous `release.yml` no longer overwrites the multi-arch manifest list published by the new `publish-agent-base-image` job; (2) the BYOI guide's broken sidecar binary download URL (`releases/download/agent-base-v<v>/spring-voyage-agent-sidecar-<target>`, a path that never existed after the asset rename in [c6e82e28](https://github.com/cvoya-com/spring-voyage/commit/c6e82e28)) is corrected to the unified, versioned form `releases/download/spring-voyage-v<v>/spring-voyage-agent-sidecar-<v>-<target>` ([#2229](https://github.com/cvoya-com/spring-voyage/issues/2229)).

- **Release artefact set narrowed and rationalised (followup to [#2229](https://github.com/cvoya-com/spring-voyage/issues/2229)).**
  - `Cvoya.Spring.Cli` is now published to nuget.org as a .NET tool: `dotnet tool install -g Cvoya.Spring.Cli`. Complements the per-RID self-contained CLI binaries already attached to each GitHub Release. The `publish-spring-cli-nuget` job in `release.yml` packs `src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj` (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>spring</ToolCommandName>`) and pushes the `.nupkg`; it emits a workflow warning and exits 0 when `NUGET_API_KEY` is not configured (same skip pattern as the retired `NPM_TOKEN` path). Other Spring Voyage packages (Core, Dapr, connector abstractions) are still not published — tracked in [#1395](https://github.com/cvoya-com/spring-voyage/issues/1395).
  - The npm package `@cvoya/spring-voyage-agent-sidecar` is no longer published with releases. BYOI conformance path 2 (custom base image embedding the bridge) is now SEA-binary-only — the per-target binary continues to ship as a GitHub Release asset. Previously published npm versions remain on npmjs.org but receive no further updates. ADR-0027 and the BYOI guide are amended to reflect the narrower path-2 contract.
  - `install.sh` is now a top-level GitHub Release asset, so `https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh` resolves directly. It previously only shipped inside the bundle tarball, which made the README's curl one-liner depend on a path that didn't exist. Intentionally NOT included in the bundle itself — `install.sh` is the pre-install bootstrap, the bundle is post-install operator tooling.
  - `Directory.Build.props` `RepositoryUrl` corrected from a personal-repo URL to the canonical `https://github.com/cvoya-com/spring-voyage`. Fixes future package-metadata leaks now that any `src/` project (starting with the CLI .NET tool) is packed.

- **Release-pipeline naming and best-practice cleanup (Waves 3 + 4 of [#2229](https://github.com/cvoya-com/spring-voyage/issues/2229)).** Finishes the consolidation begun in [#2232](https://github.com/cvoya-com/spring-voyage/pull/2232):
  - GitHub Release title is now `Spring Voyage v<version>` (was bare `v<version>`).
  - Dispatcher release asset renamed `spring-dispatcher-<v>-<rid>.{tar.gz,zip}` → `spring-voyage-dispatcher-<v>-<rid>.{tar.gz,zip}` for parity with the deployment bundle (`spring-voyage-<v>-bundle.tar.gz`) and sidecar binaries (`spring-voyage-agent-sidecar-<v>-<target>`). The CLI asset remains `spring-<v>-<rid>.{tar.gz,zip}` — `spring` is the operator-facing command name. The internal host-service name `spring-dispatcher`, the `Cvoya.Spring.Dispatcher` binary, and the `spring-dispatcher.pid` file are unchanged.
  - **Draft-then-finalize release flow.** `release.yml` now creates the GitHub Release as a draft immediately after the CI gate succeeds (`create-draft-release` job), every publish job attaches its assets to the draft, and a final `finalize-release` job promotes the draft to published only after every publish job has succeeded. Consumers no longer see partially-published releases on the Releases page when a publish job fails mid-run.
  - **`manifest.json` schema bumped 1 → 2.** Redundant fields `dispatcher_version` and `cli_version` are removed (they always equalled the top-level `version`); the new bundle pins only `version` and `platform_image`. The installer reads only `platform_image` today, so this is a no-op for current operators; the schema bump signals the contract change.
  - Residual `continue-on-error` comment in `release.yml` dropped.
  - Documentation pass: full rewrite of `docs/developer/releases.md` for the unified pipeline (one tag / one workflow / one Release, draft-then-finalize behaviour, prefix-stripped-SemVer rule for embedded version strings); colloquial-vs-canonical clarifier in `docs/architecture/agent-images.md`; `BRIDGE_VERSION` lockstep explainer in `docs/guide/operator/byoi-agent-images.md`; ADR-0042 amended for `bundle_schema_version: 2` and the unified-tag prefix; ADR-0027 and ADR-0034 amended where they listed the deleted auxiliary workflows.

- **Platform image now published multi-arch (`linux/amd64`, `linux/arm64`).** Closes
  the arm64 installation gap: `install.sh` already detects and supports `linux-arm64`
  RID and the unified release publishes all agent images as multi-arch, but
  `ghcr.io/cvoya-com/spring-voyage` was the last remaining single-arch artefact.
  Operators on arm64 hosts (Apple Silicon Podman, bare arm64 Linux servers) now
  get a native arm64 image instead of an amd64 image running under emulation.
  Implementation: the `publish-platform-image` job in `release.yml` is converted
  from raw `docker build` to `docker/build-push-action@v7` with `platforms:
  linux/amd64,linux/arm64`, matching the pattern used by `publish-agent-base-image`
  and `publish-oss-agent-images` ([#2233](https://github.com/cvoya-com/spring-voyage/issues/2233)).

- **Announce version family aligned to `1.0.0-alpha.<date>`.** The first public pre-release is cut as `v1.0.0-alpha.<yyyymmdd>` (dot-separated SemVer prerelease). Version markers in `src/Cvoya.Spring.Web/package.json`, the MCP server `serverInfo` payload, release-doc examples, installer/release scripts, and operator/architecture deployment docs are aligned to the new family. Issue-tracker milestones (`v0.1`, `v0.2`) and `docs/plan/v0.1/` are planning buckets and retain their names.

#### Operations & deployment

- **Operator wrapper renamed from `spring-voyage` to `voyage`.** The wrapper that ships with `install.sh` (`status | logs | restart | version | install | uninstall`) is now `~/.local/bin/voyage`; the checked-in source moves from `devops/install/spring-voyage` to `devops/install/voyage`. `spring` (the developer/tenant CLI) and `spring-voyage` shared a prefix that collided with shell autocomplete and visual scanning; the product name "Spring Voyage" splits cleanly into `spring` (developer/tenant) and `voyage` (operator/platform). Pure rename — no functional changes. Landed before the first `v1.0.0-alpha.<date>` release, so no operator has installed the wrapper yet and there is no migration cost.

#### Single-identity model (#1629 — pre-v0.1 cutover)

- **Every actor (unit, agent, human, connector, tenant) has exactly one stable identifier — a `Guid`.** Slugs are gone from the persistence, routing, and addressing layers. `display_name` is presentation-only — never unique, never addressable, never a foreign-key target. The membership graph rooted at the tenant is the addressing fabric. See [ADR-0036](docs/decisions/0036-single-identity-model.md), [`docs/architecture/identifiers.md`](docs/architecture/identifiers.md), and the amendment block atop [ADR-0023](docs/decisions/0023-flat-actor-ids.md). Landed in [#1637](https://github.com/cvoya-com/spring-voyage/pull/1637) as a single `InitialBaseline` migration that consolidates every prior schema; `OssTenantIds` and `GuidFormatter` provide the canonical identity helpers.
- **Database schema reset — pre-#1637 dev databases must be dropped and recreated.** The single-identity baseline collapses every prior migration into one `InitialBaseline`; there is no incremental upgrade path. v0.1 has not shipped, so no live data is affected. The operator playbook for the cutover is recorded in #1629's PR3 implementation notes ([#1637](https://github.com/cvoya-com/spring-voyage/pull/1637)).
- **OSS default tenant id is now a deterministic v5 UUID** — `OssTenantIds.Default` (`dd55c4ea-8d72-5e43-a9df-88d07af02b69`; no-dash form `dd55c4ea8d725e43a9df88d07af02b69`), pinned as a literal in `src/Cvoya.Spring.Core/Tenancy/OssTenantIds.cs`. Replaces the prior literal `"default"` string. The class also exposes `DefaultDashed` and `DefaultNoDash` `const string` literals for grep-ability across configs, dashboards, and audit logs ([#1637](https://github.com/cvoya-com/spring-voyage/pull/1637)).
- **Address is `(Scheme, Guid)` — wire form `scheme:<32-hex-no-dash>`.** `agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7` replaces every prior `agent://team/ada` shape. Path-shaped, navigation, and `@<uuid>` forms no longer parse anywhere. Parsers are lenient — the dashed Guid form is accepted on every input surface (URL paths, `Address.TryParse`, `GuidFormatter.TryParse`, the CLI resolver, the JSON converter); emit always uses the canonical no-dash form on URLs and address strings. Repository signatures, message-router internals, execution dispatchers, and tests are Guid-typed end-to-end ([#1637](https://github.com/cvoya-com/spring-voyage/pull/1637)).
- **Display-name validation rejects Guid-shaped names** (`code = display_name_is_guid_shape`) on every entity create/update surface (agent, unit, platform tenant). Empty / whitespace, control characters, and lengths over 256 chars are rejected with their own structured codes (`display_name_is_empty`, `display_name_contains_control_chars`, `display_name_too_long`). Implemented in `Cvoya.Spring.Core.Validation.DisplayNameValidator` and surfaced through 400 `ProblemDetails` responses ([#1640](https://github.com/cvoya-com/spring-voyage/pull/1640) — closes [#1632](https://github.com/cvoya-com/spring-voyage/issues/1632)).
- **Public DTOs flip stable identifiers from `string` to `Guid`** — `AgentResponse.Id`, `CreateAgentRequest.UnitIds`, `UnitResponse.Id`, `CreateUnitRequest.ParentUnitIds`, `UnitLifecycleResponse.UnitId`, `SetHumanPermissionResponse.HumanId`, `UnitForceDeleteResponse.UnitId`, `ConnectorUnitBindingResponse.UnitId`, `TenantResponse.Id`, `CreateTenantRequest.Id`. The OpenAPI document advertises `format: uuid`; Kiota and the openapi-typescript schema pick up the typed shape. **JSON DTO bodies emit dashed `8-4-4-4-12`** so Kiota's `GetGuidValue()` and STJ's default `Utf8JsonReader.GetGuid()` deserialise without a custom converter; URLs and address strings stay no-dash. `NoDashGuidJsonConverter` keeps parse lenient on JSON. `ParticipantRef.displayName` is now resolved server-side and is non-empty on every wire-form participant reference (deleted entities surface as `<deleted>`); the dashboard's prior UUID-shape heuristic is dropped. Exception-handler middleware honours `BadHttpRequestException.StatusCode`, so a malformed Guid in a JSON body returns 400, not 500 ([#1643](https://github.com/cvoya-com/spring-voyage/pull/1643) — closes [#1635](https://github.com/cvoya-com/spring-voyage/issues/1635); follow-ups [#1646](https://github.com/cvoya-com/spring-voyage/pull/1646), [#1647](https://github.com/cvoya-com/spring-voyage/pull/1647), [#1648](https://github.com/cvoya-com/spring-voyage/pull/1648)).
- **Manifest grammar — IaC local symbols within a file, Guids across packages.** Inside a manifest, references between artefacts use local symbols scoped to the file (the artefact's `name` / `id` field IS the symbol; the install pipeline mints a fresh `Guid` per artefact via `LocalSymbolMap` and binds the symbol to it). Cross-package references are 32-char no-dash hex Guids; display-name lookup across packages is gone (names aren't unique, so resolving by name across the catalog would silently bind to the wrong target). Path-style references (`unit://eng/backend/alice`) are rejected by `LocalSymbolValidator.RejectPathStyleReference` with an actionable error pointing at the new grammar ([#1642](https://github.com/cvoya-com/spring-voyage/pull/1642)).
- **CLI `show` verbs accept Guid-or-name with disambiguation.** `spring agent show <id-or-name>` and `spring unit show <id-or-name> [--unit <parent-name-or-guid>]` short-circuit to a direct lookup when the argument parses as a Guid; otherwise the resolver runs a `display_name` search (case-insensitive, exact) against the tenant and renders 0 / 1 / n results — the n-match case prints a disambiguation table keyed on Guid and exits non-zero so the caller can re-run with the chosen id. Resolution lives in `CliResolver`; rendering in `CliResolutionPrinter` ([#1650](https://github.com/cvoya-com/spring-voyage/pull/1650)).
- **`MessageReceived` activity events surface real summaries instead of the synthesised `Received {Type} message …` envelope.** `AgentActor`, `HumanActor`, and `UnitActor` call `MessageReceivedDetails.BuildSummary`, which centralises the body-as-summary / non-leaky-placeholder logic. The portal's defensive envelope-strip regex is gone ([#1641](https://github.com/cvoya-com/spring-voyage/pull/1641), [#1647](https://github.com/cvoya-com/spring-voyage/pull/1647) — closes [#1636](https://github.com/cvoya-com/spring-voyage/issues/1636), [#1639](https://github.com/cvoya-com/spring-voyage/issues/1639)).

- Migrated test assertions from FluentAssertions to Shouldly ([#157](https://github.com/cvoya-com/spring-voyage/pull/157)).
- All 4xx endpoint responses now return `ProblemDetails` ([#192](https://github.com/cvoya-com/spring-voyage/pull/192)).
- `JsonStringEnumConverter` registered globally for API enum serialization ([#153](https://github.com/cvoya-com/spring-voyage/pull/153)).
- Agent-creation flow: creator granted Owner on unit creation; `MessageRouter` skipped for member adds ([#328](https://github.com/cvoya-com/spring-voyage/pull/328)).
- Worker is the single owner of EF Core migrations ([#318](https://github.com/cvoya-com/spring-voyage/pull/318)).
- EF Core migrations adopted for the Dapr `DbContext` ([#237](https://github.com/cvoya-com/spring-voyage/pull/237)).
- Roadmap restructured to reflect actual completion status and introduce issue-tracked planning ([#419](https://github.com/cvoya-com/spring-voyage/pull/419)).
- **GitHub connector configuration UX** — the create-unit wizard and the unit's Connector tab dropped the manual owner / repo / installation-id triple in favour of a single Repository dropdown (aggregated across the visible installations via the new `GET /api/v1/connectors/github/actions/list-repositories` endpoint) and a Default reviewer dropdown sourced from `GET /api/v1/connectors/github/actions/list-collaborators`. Adds a `reviewer` field to `UnitGitHubConfig` and a `--reviewer` flag to `spring connector bind`. The visible "App installation" picker is gone — installation ids ride along on each repository row, eliminating the cross-tenant enumeration surface in OSS and giving cloud deployments a clean extension point to scope the listing per OAuth session ([#1133](https://github.com/cvoya-com/spring-voyage/issues/1133)).

### Removed

- **Breaking:** the `Secrets:AllowEphemeralDevKey` flag and its in-memory random-key fallback. The platform now requires a real AES-256 key on every deployment via `SPRING_SECRETS_AES_KEY` (env) or `Secrets:AesKeyFile` (mounted file); a startup configuration validator refuses to come up otherwise. Rationale: a per-process random key cannot work in the platform's multi-process topology — `spring-api` and `spring-worker` share the same encrypted secret store, and an in-memory key meant the worker silently failed to decrypt secrets the API host wrote. **Migration:** run `devops/deploy/deploy.sh init` (added in this release) to provision `spring.env` and a freshly-generated key in one step, then re-set any tenant- or unit-scoped secrets that were stored under the old random key (existing ciphertexts are unreadable under the new key, as expected). Operators who prefer to manage the file by hand can `openssl rand -base64 32` and set `SPRING_SECRETS_AES_KEY=` in `devops/deploy/spring.env` themselves; `deploy.sh up` validates a non-placeholder value at start time and fails fast with a precise message when missing.
- Legacy `v`-prefixed OCI tag (`ghcr.io/cvoya-com/spring-voyage-agent-base:vX.Y.Z`) from the agent-base release workflow; the unprefixed `:X.Y.Z` and `:latest` tags continue to be published ([#1121](https://github.com/cvoya-com/spring-voyage/issues/1121)).
- Hosted (in-process) execution path; all agentic work now delegated to containers ([#118](https://github.com/cvoya-com/spring-voyage/pull/118)).
- Container-launch responsibilities removed from unit start/stop API endpoints ([#373](https://github.com/cvoya-com/spring-voyage/pull/373)).
- PostgreSQL statestore component removed from local dev in favour of the Dapr state store wrapper ([70d6565](https://github.com/cvoya-com/spring-voyage/commit/70d6565)).

### Fixed

- **Catalog package installs no longer fail with `ImagePullFailed` / `manifest unknown` during the pre-release line.** Catalog packages pin the rolling `:latest` image tag (e.g. `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest`), but `release.yml` only published the `:latest` container tags for **stable** releases — so while every release is a pre-release, `:latest` was never pushed and installs could not pull it. `eng/release/release.sh --latest` (and a `mark_latest` `workflow_dispatch` input for promoting an existing tag) now opt a chosen pre-release into moving both the container `:latest` tags and the GitHub Release "Latest" badge; intent travels on an annotated-tag `Mark-Latest: true` trailer read by `release.yml`'s `resolve` job. The `:MAJOR_MINOR` channel tag stays stable-only, and `release.sh` now also verifies the `:latest` tag (not just `:<version>`) when a release is latest-bearing ([#2970](https://github.com/cvoya-com/spring-voyage/issues/2970)).
- Repoint `savasp/spring-voyage` URLs in README CI badge, quick-start docs, and ADRs 0001–0004 to the canonical `cvoya-com/spring-voyage` ([#2186](https://github.com/cvoya-com/spring-voyage/issues/2186)).
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
