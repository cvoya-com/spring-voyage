# Area G: Code review + decomposition

**Status:** ✅ **Done.** Umbrella [#1221](https://github.com/cvoya-com/spring-voyage/issues/1221) closed. Discovery completed; cleanup sub-issues were populated and executed. No ADR-0029 boundary violations were found in existing code.

## Stage 0 — complete ✅

`agents/dapr-agent/agent.py` already dropped the Dapr-Workflow wrapper (cites "ADR 0029 Stage 0"). Uses a plain-Python tool-calling loop with `DaprChatClient` + MCP tool proxies.

## Scope

- **Discovery ✅:** Done. Five new structural-cleanup issues plus four pre-existing `area:code-cleanup` issues are tracked under #1221.
- **PRs (later):** Targeted cleanup PRs once D establishes new boundaries.

## Boundary violations — none found

No ADR-0029 boundary violations in existing code. `ILlmDispatcher` / `DispatcherProxiedLlmDispatcher` is platform-internal (worker host process only). `DaprChatClient` in `dapr-agent` is targeted for retirement in Area D Stage 3 — not a current violation.

## Sequencing notes for Area D

Two structural cleanups should land **before** Area D Stage 3 changes hit those files (reduces merge-conflict cost):

- `A2AExecutionDispatcher.cs` extraction — the primary surface for Area D's new A2A/tenant boundary changes.
- `ServiceCollectionExtensions.cs` split — Area D Stage 3 will add new boundary registrations to this 1,130-line monolith.

`AgentActor.cs` decomposition is the highest-priority structural cleanup for code health (2,190 lines, 7 concerns), but its self-call cleanup pattern is the platform-side half of the tenant execution boundary — Area D should understand the pattern before redesigning the tenant-to-platform API surface.

## Dependencies

- Discovery: pre-work ✅ done.
- Cleanup PRs: depend on D for boundary direction; the two pre-D enablers above are the exceptions.
