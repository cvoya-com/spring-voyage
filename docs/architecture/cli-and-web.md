# CLI & Web

> **[Architecture Index](README.md)** | Related: [Security](security.md), [Deployment](deployment.md), [Units & Agents](units.md)

---

## Client API Surface


| API Domain                | Operations                                                              |
| ------------------------- | ----------------------------------------------------------------------- |
| **Identity & Auth**       | API token CRUD, token invalidation, user management |
| **Unit Management**       | CRUD, configure AI/policies/connectors, manage members                  |
| **Agent Management**      | CRUD, view status, configure expertise                                  |
| **Messaging**             | Send to agents/units, read conversations                                |
| **Activity Streams**      | Subscribe via SSE/WebSocket                                             |
| **Workflow Management**   | Start/stop/inspect, approve human-in-the-loop steps                     |
| **Directory & Discovery** | Query expertise, browse capabilities                                    |
| **Package Management**    | Install/remove, browse registry                                         |
| **Observability**         | Metrics, cost tracking, audit logs                                      |
| **Admin**                 | User management, tenant config                                          |


## Hosting Modes

The API Host and Worker Host are separate binaries. The "daemon" mode is the API Host running in a single-tenant, auth-disabled configuration — not a separate binary. This simplifies local development while keeping a single codebase.

```mermaid
graph TD
    subgraph core["Spring.Core + Spring.Dapr<br/>(same interfaces, same behavior)"]
        api["API Host<br/>(REST + WS + authz)<br/>local: single-tenant, no auth"]
        worker["Worker Host<br/>(headless, config-driven)"]
    end
    api --> cli["CLI"]
    api --> web["Web Portal"]
    api --> native["Native Apps"]
```



## The `spring` CLI Command

The `Spring.Cli` project produces the `spring` command-line tool:

```
spring unit list
spring agent status ada
spring message send agent://engineering-team/ada "Review PR #42"
spring conversation list --unit engineering-team
spring conversation show c-1834
spring conversation send --conversation c-1834 agent://engineering-team/ada "Ship it."
spring inbox list
spring inbox respond c-1834 "Approved"
spring activity stream --unit engineering-team
spring connector catalog
spring connector bind --unit engineering-team --type github --owner my-org --repo platform
spring connector show --unit engineering-team
spring directory list
spring directory show python/fastapi
spring directory search "refactor python"
spring connector bindings github
spring build packages/software-engineering
spring apply -f units/engineering-team.yaml
spring workflow status software-dev-cycle
spring images list
```

### Directory verbs

The `spring directory` family mirrors the portal's `/directory` surface over the shared `POST /api/v1/directory/search` endpoint. Every verb takes `--output table|json`, `--inside` (request the inside-the-unit boundary view), and the usual `--domain`/`--owner`/`--typed-only`/`--limit`/`--offset` filters.

| Verb | Purpose | Portal equivalent |
|------|---------|-------------------|
| `spring directory list` | Enumerate every directory entry (subject to filters + boundary). Omits the score column since there's no free-text query to rank against. | `/directory` with the search box empty. |
| `spring directory show <slug>` | Render a single entry — slug, domain, owner + display name, aggregating unit, ancestor chain breadcrumb, typed-contract flag, match reason, score, and the `projection/{slug}` path set. | Click a row on `/directory`. |
| `spring directory search "<text>"` | Free-text query matched against slug, display name, description, and tags; ranks exact slug > exact domain > text relevance > aggregated-coverage. | The search box on `/directory`. |

`show` carries the full owner-chain + projection-path detail (#553): the ancestor chain renders as a `unit://mid -> unit://root` breadcrumb reading from the closest projecting ancestor up to the highest, and each `projection/{slug}` path listed under "Projected via" identifies one surfacing ancestor. A direct hit renders both as `(direct)` with no "Projected via" block.

**Distribution modes:**

- **dotnet tool:** `dotnet tool install -g spring-cli`. Requires .NET SDK. Updated via `dotnet tool update -g spring-cli`.
- **Standalone executable:** Published as a self-contained single-file app via `dotnet publish`. No .NET SDK required. Distributed via GitHub releases, Homebrew, or direct download.

The command name is `spring` in both cases.

## Deployment Topology


| Environment              | Topology                                                                                                                                                                                          |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Local dev**            | API Host (single-tenant mode) + Dapr sidecar + Podman containers. Single machine. `spring` CLI for interaction.                                                                                   |
| **Staging / small prod** | API Host + Worker Host behind a reverse proxy. Docker Compose with Dapr sidecars. PostgreSQL + Redis.                                                                                             |
| **Production**           | Kubernetes with Dapr operator. API Host replicas behind load balancer. Worker Hosts scaled by workload. Execution environments as ephemeral pods. Kafka for pub/sub. Pluggable secret store. |
