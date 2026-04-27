# Area G: Code review + decomposition

**Status:** 🟢 **Discovery done.** Five new cleanup issues created (#1276–#1280), all wired under #1221. Pre-existing issues (#940, #939, #1200, #1043) labeled `area:code-cleanup`. Cleanup PRs gated on Area D establishing boundaries.

## Sub-issues (v0.1)

| # | Title | Notes |
|---|---|---|
| [#1276](https://github.com/cvoya-com/spring-voyage/issues/1276) | cleanup: decompose AgentActor.cs (2,190 lines, 7 concerns, 15 deps) | Highest priority; critical-path for every agent message |
| [#1277](https://github.com/cvoya-com/spring-voyage/issues/1277) | cleanup: extract IAgentTransport from A2AExecutionDispatcher (789 lines) | Do before Area D changes hit this file |
| [#1278](https://github.com/cvoya-com/spring-voyage/issues/1278) | cleanup: split ServiceCollectionExtensions.cs monolith (1,130 lines) | Do before Area D adds new boundary registrations |
| [#1279](https://github.com/cvoya-com/spring-voyage/issues/1279) | cleanup: add timeout-path tests for A2AExecutionDispatcher | Test gap for 60s readiness probe + 5min task-poll timeouts |
| [#1280](https://github.com/cvoya.spring-voyage/issues/1280) | cleanup: extract validation/membership from UnitActor.cs (1,401 lines) | Start with validation scheduling (cleanest seam) |
| [#940](https://github.com/cvoya-com/spring-voyage/issues/940) | Migrate dapr-agent to a2a-sdk 1.x | Pre-existing |
| [#939](https://github.com/cvoya-com/spring-voyage/issues/939) | Defer agent-runtime validation to unit-start time | Pre-existing |
| [#1200](https://github.com/cvoya-com/spring-voyage/issues/1200) | Copying an agent's identity doesn't copy the unique path | Pre-existing |
| [#1043](https://github.com/cvoya-com/spring-voyage/issues/1043) | Track upstream Kiota fix for nullable oneOf wrappers | Pre-existing |

## Stage 0 — complete ✅

`agents/dapr-agent/agent.py` already dropped the Dapr-Workflow wrapper (cites "ADR 0029 Stage 0"). Uses a plain-Python tool-calling loop with `DaprChatClient` + MCP tool proxies.

## Scope

- **Discovery ✅:** Done. Five new issues + four pre-existing tagged.
- **PRs (later):** Targeted cleanup PRs. #1277 and #1278 should land **before** Area D changes hit those files (reduces merge-conflict cost). #1276 (AgentActor) is the highest-priority structural cleanup. All PRs after D is underway.

## Boundary violations — none found

No ADR-0029 boundary violations in existing code. `ILlmDispatcher` / `DispatcherProxiedLlmDispatcher` is platform-internal (worker host process only). `DaprChatClient` in `dapr-agent` is targeted for retirement in Area D Stage 3 — not a current violation.

## Key area D interaction

- `A2AExecutionDispatcher.cs` (#1277) is the primary surface for Area D's new A2A/tenant boundary changes — decompose it first.
- `ServiceCollectionExtensions.cs` (#1278) will need new Area D registrations — split it first.
- `AgentActor.cs` (#1276) dispatch coordination (the self-call cleanup pattern, "RunDispatchAsync outside actor turn" constraint) is the platform-side half of the tenant execution boundary — Area D should understand this before designing the tenant-to-platform API.

## Dependencies

- Discovery: pre-work ✅ done.
- Cleanup PRs: depend on D (new boundaries inform decomposition direction). #1277, #1278 can land earlier as enablers for D.
