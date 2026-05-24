# 0055 — Pull-based agent bootstrap and workspace delivery

- **Status:** Proposed — 2026-05-22. Replaces the dispatcher push/seed model for agent workspace and configuration with a worker-hosted pull endpoint that the agent-sidecar fetches on launch and re-checks on every turn. Folds the co-hosting consequence from [#2639](https://github.com/cvoya-com/spring-voyage/issues/2639): the per-agent workspace mount path stops being a global constant and becomes always-per-member at `/spring/members/<memberId>/`, including the standalone case.
- **Date:** 2026-05-22
- **Related ADRs:** [0029](0029-tenant-execution-boundary.md) — the per-agent persistent volume and `IAgentContext` bootstrap shape this record changes the *delivery mechanism* for. [0027](0027-agent-image-conformance-contract.md) — the A2A 0.3.x in-container surface that hosts the new bootstrap pull alongside `message/send`. [0028](0028-tenant-scoped-runtime-topology.md) — the network topology the bootstrap call traverses (same plane the per-turn MCP call already crosses). [0054](0054-one-mcp-server-one-execution-host.md) — the worker is the execution host that already owns prompt assembly, `IAgentContextBuilder`, the per-turn MCP session authority, and the connector resolvers; the bootstrap endpoint sits next to that. [0052 (archived)](archive/0052-execution-host-roles-and-single-mcp-server.md) — the per-turn `mcpToken` delivery wire (A2A `message/send` metadata) this record explicitly does **not** change.
- **Related issues:** [#2637](https://github.com/cvoya-com/spring-voyage/issues/2637) — this ADR. [#2639](https://github.com/cvoya-com/spring-voyage/issues/2639) — the per-agent volume root being `root:root` is a symptom of push/seed; the pull model dissolves it. [#2608](https://github.com/cvoya-com/spring-voyage/issues/2608) — single workspace mount consolidation; the pull model keeps "one workspace mount per member" but moves the file-writer from the dispatcher into the sidecar. [#2615](https://github.com/cvoya-com/spring-voyage/issues/2615) / [#2620](https://github.com/cvoya-com/spring-voyage/pull/2620) — the per-turn MCP token wire; kept intact. [#2648](https://github.com/cvoya-com/spring-voyage/pull/2648) — the chmod-1777 stopgap this ADR is the long-term resolution for.
- **Related code:** `src/Cvoya.Spring.Dispatcher/WorkspaceMaterializer.cs`, `src/Cvoya.Spring.Core/Execution/IWorkspaceVolumePopulator.cs`, `src/Cvoya.Spring.Dapr/Execution/ProcessContainerRuntime.cs` (`TryPopulateVolumeAsync`), `src/Cvoya.Spring.Dapr/Execution/AgentVolumeManager.cs`, `src/Cvoya.Spring.Core/Execution/AgentWorkspaceContract.cs`, `src/Cvoya.Spring.Core/Execution/IAgentRuntimeLauncher.cs` (`AgentLaunchSpec.WorkspaceFiles` / `ContextFiles`), `src/Cvoya.Spring.AgentRuntimes/Launchers/*.cs`, `src/Cvoya.Spring.AgentSidecar/src/a2a.ts`, `src/Cvoya.Spring.AgentSidecar/src/mcp-config.ts`.

## Context

The current delivery mechanism for a per-agent workspace and per-invocation configuration is a **push/seed** model:

1. Each launcher (`ClaudeCodeLauncher`, `CodexLauncher`, `GeminiLauncher`, `SpringVoyageAgentLauncher`) produces an `AgentLaunchSpec` whose `WorkspaceFiles` / `ContextFiles` maps carry the file contents (`CLAUDE.md`, `.mcp.json`, `agent-definition.yaml`, `tenant-config.json`, connector contributions).
2. The dispatcher's `WorkspaceMaterializer` writes those files into a staging directory on the dispatcher host filesystem.
3. `IWorkspaceVolumePopulator.TryPopulateVolumeAsync` (implemented on `ProcessContainerRuntime`) runs a helper container that mounts the per-agent named volume and copies the staged files into it — runtime-mediated because the dispatcher and the runtime's volume storage do not share a filesystem under `podman machine` on macOS.
4. The dispatcher then starts the agent container; the volume already carries the files.

This shape has accumulated three structural problems:

- **It needs a helper container to write into the agent's own volume.** The dispatcher is off-filesystem from the runtime by design ([ADR-0028](0028-tenant-scoped-runtime-topology.md)); the only way it can populate a named volume is through the runtime. That helper is what [#2648](https://github.com/cvoya-com/spring-voyage/pull/2648) just fixed a 500 in.
- **The agent container is never the volume's first mounter.** Image volume pre-population — the idiomatic mechanism a container image uses to put correct ownership and seed content on a named volume on first mount — is defeated. [#2639](https://github.com/cvoya-com/spring-voyage/issues/2639) is forced to ship a chmod-1777 stopgap to make the root-owned volume writable by the agent uid.
- **It is launch-time only.** The dispatcher populates the workspace before first start, then never again. A change to platform-layer instructions (`CLAUDE.md`) on a long-running persistent agent never reaches the running container. The per-turn `.mcp.json` token rewrite ([#2615](https://github.com/cvoya-com/spring-voyage/issues/2615) / [#2620](https://github.com/cvoya-com/spring-voyage/pull/2620)) bolts onto the sidecar specifically because the volume contents are otherwise frozen.

The forcing question this ADR answers is: who owns the file contents inside the agent's persistent volume? Today the answer is *the dispatcher writes them, the sidecar amends one of them per turn, the agent runtime may overwrite any of them mid-turn*. This ADR makes it: **the sidecar writes them, sourced from a worker-hosted pull endpoint, and verifies the platform-authoritative subset on every turn.**

## Decision

### 1. The agent-sidecar pulls its workspace and configuration from a worker-hosted bootstrap endpoint

The sidecar runs PID-1-supervised inside the agent container ([ADR-0027](0027-agent-image-conformance-contract.md)) and already brokers per-turn `.mcp.json` rewrites. Adding a fetch step to it is incremental, not a new component. The sidecar gates HTTP listen on a successful first bootstrap fetch — the container reports A2A-ready only after its workspace is materialised.

The dispatcher's role narrows to lifecycle: provision the per-agent named volume (empty), start the container, tear it down. It no longer writes files anywhere on its own host filesystem or into the volume.

### 2. The worker hosts the bootstrap endpoint

Per [ADR-0054](0054-one-mcp-server-one-execution-host.md) the worker is the execution host and already owns prompt assembly, `IAgentContextBuilder`, the per-turn MCP session authority, and the connector resolvers — every input to the bundle. Hosting the bootstrap endpoint on the worker keeps the bundle assembly co-located with its inputs; hosting it on the dispatcher would require re-importing all of that knowledge across the worker/dispatcher boundary.

The bootstrap surface is a route on a dedicated Kestrel endpoint on the worker, alongside the `POST /mcp/` route ([ADR-0054](0054-one-mcp-server-one-execution-host.md) §2). The agent container reaches it on the same network plane it already reaches `SPRING_MCP_URL` ([ADR-0028](0028-tenant-scoped-runtime-topology.md)).

### 3. Bootstrap wire contract

```
GET  /v1/bootstrap/agents/{agentId}
     Authorization: Bearer <SPRING_BOOTSTRAP_TOKEN>
     If-None-Match: "<etag>"
     Accept: application/json

200 OK
     ETag: "sha256:<bundle-hash>"
     Cache-Control: no-cache
     {
       "version": "sha256:<bundle-hash>",
       "issuedAt": "2026-05-22T12:00:00Z",
       "files": [
         { "path": "CLAUDE.md",                          "sha256": "...", "content": "..." },
         { "path": ".mcp.json",                          "sha256": "...", "content": "..." },
         { "path": "context/agent-definition.yaml",      "sha256": "...", "content": "..." },
         { "path": "context/tenant-config.json",         "sha256": "...", "content": "..." },
         { "path": "connectors/<slug>/<file>",           "sha256": "...", "content": "..." }
       ],
       "platformFileHashes": {
         "CLAUDE.md":  "sha256:...",
         ".mcp.json":  "sha256:..."
       }
     }

304 Not Modified
```

- `version` and the ETag are the content-addressable sha256 of the canonical JSON serialisation of the bundle. Two workers serving the same agent definition compute the same hash; re-pulls after a worker restart pay zero bandwidth on a 304.
- Paths are workspace-relative; the sidecar writes them under `SPRING_WORKSPACE_PATH` (§5) at sidecar-chosen subpaths. The dispatcher does not see them.
- `platformFileHashes` is the SV-authoritative subset of `files` — the files §6's integrity check pins. Connector contributions and `context/*` are part of the bundle but are not pinned per-turn.
- The bundle is content-only. The per-turn MCP token is **not** part of it (§4).

### 4. The per-turn MCP token continues to ride A2A `message/send` metadata

The wire established by [#2615](https://github.com/cvoya-com/spring-voyage/issues/2615) / [#2620](https://github.com/cvoya-com/spring-voyage/pull/2620) — the worker stamps `mcpToken` on the `message/send` metadata; the sidecar rewrites the `spring-voyage` MCP server block's `Authorization` header in `.mcp.json` before each CLI spawn — is unchanged.

The lifetimes do not match: the MCP session token is per-turn (issued at dispatch start, revoked at turn end, [ADR-0054](0054-one-mcp-server-one-execution-host.md) §5); the bootstrap bundle is per-agent-configuration. Folding the token into the bootstrap response would either force a bootstrap pull on every turn (defeats caching) or extend the token's effective lifetime past one turn (loses the hard-revoke property). Keeping them on separate wires keeps each wire's lifetime model coherent.

### 5. Workspace mount path is always per-member, including standalone

Every agent container — including the one-member "standalone" case — gets its workspace mounted at `/spring/members/<memberId>/`. The `/spring/workspace/` constant from `AgentWorkspaceContract.WorkspaceMountPath` is deleted; the only valid way to resolve the path becomes the per-process `SPRING_WORKSPACE_PATH` env var.

A uniform path shape collapses two cases (standalone vs co-hosted) into one. Co-hosting becomes a placement decision: mount one per-member volume or N, at N distinct `/spring/members/<id>/` paths. No data migration when a member moves between containers; no carve-out anywhere in the code that special-cases standalone.

`SPRING_WORKSPACE_PATH` retains its name (it points at the agent's per-member workspace). The constant `AgentWorkspaceContract.WorkspaceMountPath` is replaced by a helper `AgentWorkspaceContract.BuildMountPath(memberId)`; the constant `WorkspaceMountPathNoSlash` is deleted (callers compose paths from the helper's return).

### 6. The sidecar verifies platform-authoritative files on every turn

Before every CLI spawn — alongside the existing per-turn `mcpToken` rewrite — the sidecar:

1. Recomputes sha256 of each path listed in `platformFileHashes` from the last-known bundle.
2. If any hash diverges from the bundle's recorded value, re-pulls the bundle (`If-None-Match` on the cached etag) and rewrites the diverged files.
3. Spawns the CLI.

This pins the platform-authoritative subset bit-for-bit on every turn. A CLI invocation that rewrites `CLAUDE.md` or `.mcp.json` mid-turn does not survive into the next turn: the next-turn integrity check restores the platform's bytes. The sidecar is the sole writer of files in `platformFileHashes`; the integrity check is what makes "sole writer" a real invariant rather than a convention.

Bootstrap fetch latency on the warm path is one HTTP call returning 304 with an empty body — bounded by the worker-container RTT. The sidecar caches the last bundle in memory; integrity-check arithmetic is sha256 over the platform files (small, no I/O beyond the file reads it already performs).

### 7. Layering rule — platform layer vs repo layer

Moved to [ADR-0058 §2.4](0058-spring-voyage-container-contract.md) — the
layering rule (platform layer vs repo layer; non-override invariant; no
assumption that an agent has a repo) is a general workspace-contract
statement, not specific to the pull-based bootstrap. The canonical
statement now lives on the container-contract ADR; this section is a
pointer.

### 8. Bootstrap auth: per-agent bearer, lifetime = agent lifetime

A per-agent bootstrap token is issued at agent-provision time (alongside `AgentVolumeManager.EnsureAsync`) and revoked on undeploy. Stamped into the launch env as `SPRING_BOOTSTRAP_TOKEN`. Scoped to `(tenantId, agentId)`; presenting it cannot fetch another agent's bundle.

The token is opaque (256-bit random secret), stored server-side in a new `IAgentBootstrapAuthStore` distinct from the per-turn MCP session store (different lifetime). A bootstrap call validates the token against the store and the agentId in the URL.

The auth model is intentionally simpler than the per-turn MCP session model — a bootstrap pull is idempotent and cacheable, so a long-lived token does not erode the "hard-revoke at turn-end" property the MCP session relies on. A future rotation/refresh mechanism is an extension, not a re-decision; named in revisit criteria.

### 9. What the container sees at launch

Moved to [ADR-0058 §1](0058-spring-voyage-container-contract.md) — the
full env-var enumeration (including the bootstrap-related entries
`SPRING_BOOTSTRAP_URL` / `SPRING_BOOTSTRAP_TOKEN` introduced by this
ADR, the workspace path `SPRING_WORKSPACE_PATH`, and the per-turn MCP
token contract) now lives on the canonical container-contract ADR. The
bootstrap-specific shape — empty per-member volume mounted at
`SPRING_WORKSPACE_PATH`, sidecar's bootstrap step gates HTTP-listen,
first `message/send` blocks until the bundle is materialised and
verified — is recorded by ADR-0058 §3 (process tree) and §4.1 (inbound
endpoint readiness).

### 10. Deletions in the implementation PR(s)

The following surfaces are removed in the work that follows ADR acceptance:

- `IWorkspaceMaterializer` and `WorkspaceMaterializer` (dispatcher).
- `IWorkspaceVolumePopulator` and `ProcessContainerRuntime.TryPopulateVolumeAsync` (the helper-container `podman cp` path).
- `AgentLaunchSpec.WorkspaceFiles`, `ContextFiles`, `ContextMountPath` (launchers no longer ship file contents).
- The dispatcher's `Dispatcher:WorkspaceRoot` option and the per-invocation staging-directory bind-mount fallback.
- `AgentWorkspaceContract.WorkspaceMountPath` and `WorkspaceMountPathNoSlash` constants.
- The chmod-1777 stopgap from [#2640](https://github.com/cvoya-com/spring-voyage/issues/2640) / [#2648](https://github.com/cvoya-com/spring-voyage/pull/2648). The agent container becomes the volume's first mounter under the pull model, so the image's declared `/spring/members/<memberId>/` ownership pre-populates onto the volume on first mount. This dissolves [#2639](https://github.com/cvoya-com/spring-voyage/issues/2639).

### 11. Additions in the implementation PR(s)

- Worker: `BootstrapEndpoints` (a Kestrel route group) hosting `GET /v1/bootstrap/agents/{agentId}`, backed by an `IAgentBootstrapBundleProvider` that composes the bundle from the prompt assembler, `IAgentContextBuilder`, the connector runtime-context resolver, and the launcher's static contribution.
- Worker: `IAgentBootstrapAuthStore` — issues per-agent bearer at `EnsureAsync`, revokes at undeploy.
- Sidecar (TS): `bootstrap.ts` — first-fetch on container start, cached etag, integrity check at every-turn cadence, materialises files under `SPRING_WORKSPACE_PATH`.
- Core: `AgentWorkspaceContract.BuildMountPath(memberId)` helper.
- Launchers: become pure env-var + argv + image declarations (no `WorkspaceFiles` / `ContextFiles` to ship). The platform-file producers move under `IAgentBootstrapBundleProvider`.

## Consequences

- **One writer per workspace file class.** The sidecar is the sole writer of `platformFileHashes` files; the integrity check makes that an invariant, not a convention. Repo-layer files remain free for the agent runtime to manipulate.
- **Post-creation updates work.** A change to a long-running persistent agent's instructions, MCP config, or connector contributions takes effect on the next turn's integrity-check / re-pull cycle — no container restart, no redeploy.
- **One mount-path shape, always.** Standalone and co-hosted agents see the same `/spring/members/<id>/` path topology. The hardcoded `/spring/workspace/` global goes away; the only valid path resolver is `SPRING_WORKSPACE_PATH`.
- **Volume ownership pre-population is restored.** The agent container is the volume's first mounter; the image declares `/spring/members/<memberId>/` with `agent:agent` ownership; the runtime pre-populates that onto the empty volume on first mount. No chown, no chmod, no helper container, no [#2639](https://github.com/cvoya-com/spring-voyage/issues/2639).
- **The dispatcher narrows.** It owns container lifecycle and nothing else inside the agent's volume. The push/seed seam disappears.
- **Auditable configuration delivery.** Every bootstrap call is one HTTP request the worker can log: which agent, which bundle hash, what changed since last fetch. Compare to the seed model, where "what files does this container have?" is whatever the dispatcher wrote at launch time plus whatever has since been overwritten by anything inside.
- **The per-turn MCP token wire is unchanged.** The work in [#2615](https://github.com/cvoya-com/spring-voyage/issues/2615) / [#2620](https://github.com/cvoya-com/spring-voyage/pull/2620) keeps its place; this ADR is additive next to it, not a replacement.
- **OTLP-ingest auth (`SPRING_CALLBACK_*`) is untouched.** Distinct surface, distinct lifetime, called out in [ADR-0054](0054-one-mcp-server-one-execution-host.md) — same treatment here.
- **A new client must exist for the agent SDK.** Containers running the Spring Voyage agent SDK (rather than a CLI behind the sidecar bridge) need a small bootstrap client of their own. The contract is small (one GET, one bearer header, one cached etag) and uniform across SDKs.

## Revisit criteria

- **Horizontal scale-out of the worker.** Same caveat as [ADR-0054](0054-one-mcp-server-one-execution-host.md) §"Revisit criteria": the `IAgentBootstrapAuthStore` is in-process; more than one worker reopens the store-placement question.
- **Bootstrap token rotation.** A per-agent bearer with agent-lifetime is sufficient for v0.1. A leak-mitigation policy (rotating tokens, refresh-on-restart) is an extension; if rotation lands, the bootstrap wire grows a refresh path but the surface above stays.
- **Pulling the per-turn MCP token through bootstrap.** Named alternative in §4; rejected for v0.1 to keep lifetimes separated. Revisited only if the bootstrap pull starts running per-turn for other reasons.
- **Streaming or push notifications for configuration updates.** The pull model with per-turn integrity check is enough for v0.1: the agent re-fetches at turn cadence, which is the cadence at which a change actually affects behaviour. A push notification (`Cache-Control: no-cache` + a sidecar long-poll) is an extension if turn cadence proves too slow for some workflow.
