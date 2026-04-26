# Managing Units and Agents

This guide covers the full lifecycle of units and agents: creation, configuration, membership management, policies, and teardown.

## Unit Lifecycle

### Creating a Unit

```
spring unit create <name> [--description "..."]
```

A unit is usable immediately after creation. You can add agents, connectors, and policies incrementally.

#### From a template

Create a unit by instantiating a packaged template:

```
spring unit create-from-template <package>/<template-name> [--name <override>] [--display <display-name>]
```

Example:

```
spring unit create-from-template software-engineering/engineering-team --name eng-team
spring unit create-from-template product-management/product-team --name pm-team
```

`--name` overrides the manifest-derived unit name so repeated instantiations of the same template don't collide. The legacy `spring unit create --from-template <package>/<template>` flag keeps working but prints a deprecation notice — use the first-class verb above.

### Listing Units

```
spring unit list
```

### Configuring a Unit

Set orchestration, execution defaults, and structure:

```
spring unit set <name> \
  --structure hierarchical \
  --ai-execution delegated \
  --ai-tool software-dev-cycle \
  --ai-environment-image spring-workflows/software-dev-cycle:latest \
  --ai-environment-runtime podman \
  --execution-image spring-agent:latest \
  --execution-runtime podman
```

### Setting Policies

Per-unit governance policies (skill, model, cost, execution mode, initiative) are edited through the unified `spring unit policy` verb group:

```
spring unit policy skill          get|set|clear <unit> [flags...]
spring unit policy model          get|set|clear <unit> [flags...]
spring unit policy cost           get|set|clear <unit> [flags...]
spring unit policy execution-mode get|set|clear <unit> [flags...]
spring unit policy initiative     get|set|clear <unit> [flags...]
```

Examples:

```
# Allow-list / block-list for tools (skills) and models.
spring unit policy skill set eng-team --allowed github,filesystem --blocked shell
spring unit policy model set eng-team --allowed claude-sonnet-4,gpt-4o --blocked gpt-3.5-turbo

# Cost caps (USD).
spring unit policy cost set eng-team --max-per-invocation 0.50 --max-per-hour 5 --max-per-day 25

# Force every agent in the unit to a single execution mode.
spring unit policy execution-mode set eng-team --forced OnDemand

# Initiative deny-overlay plus ceiling level.
spring unit policy initiative set eng-team --max-level Proactive --blocked agent.spawn
```

Alternatively, pass a YAML fragment for the same dimension:

```
spring unit policy skill set eng-team -f path/to/skill-policy.yaml
```

`spring unit policy <dimension> get <unit>` prints the current slot plus the inheritance chain (today a single hop — see [#414](https://github.com/cvoya-com/spring-voyage/issues/414) for parent-unit overlay). `clear` removes a dimension without touching the other four.

### Orchestration Strategy

Pick the `IOrchestrationStrategy` a unit dispatches through on every domain message. Mirrors the `GET/PUT/DELETE /api/v1/units/{id}/orchestration` surface introduced in #606 — editing the same `orchestration.strategy` slot a `spring apply -f unit.yaml` manifest writes, without needing a full re-apply.

```
spring unit orchestration get   <unit>
spring unit orchestration set   <unit> --strategy {ai|workflow|label-routed} [--label-routing <file>]
spring unit orchestration clear <unit>
```

- `get` prints the persisted strategy plus the unit's `UnitPolicy.LabelRouting` block (what the `label-routed` strategy consumes).
- `set` writes the slot. Only the platform-offered keys are whitelisted today; host-registered custom keys are tracked under [#605](https://github.com/cvoya-com/spring-voyage/issues/605).
- `set --label-routing <file>` is a UI-parity convenience: it also applies the YAML fragment as a `UnitPolicy.LabelRouting` block through the existing `/api/v1/units/{id}/policy` endpoint. Accepts either a bare dimension map (`triggerLabels:`, `addOnAssign:`, `removeOnAssign:`) or a top-level `labelRouting:` / `label-routing:` wrapper — the same tolerance `spring unit policy label-routing set -f` applies.
- `clear` removes the slot; the resolver falls back to `UnitPolicy.LabelRouting`-inferred `label-routed` when set, otherwise the unkeyed platform default (ADR-0010).

Writes invalidate the in-process resolver cache so the next message dispatched to the unit sees the new strategy immediately.

### Execution defaults (#601 B-wide)

Units and agents share a five-field `execution:` block (`image`, `runtime`, `tool`, `provider`, `model`). The unit block acts as the default inherited by member agents that don't declare their own value — see `docs/architecture/units.md § Unit execution defaults` for the full agent → unit → fail resolution chain.

```
spring unit execution get   <unit>
spring unit execution set   <unit> [--image …] [--runtime docker|podman] [--tool …] [--provider …] [--model …]
spring unit execution clear <unit> [--field image|runtime|tool|provider|model]

spring agent execution get   <agent>
spring agent execution set   <agent> [--image …] [--runtime …] [--tool …] [--provider …] [--model …] [--hosting ephemeral|persistent]
spring agent execution clear <agent> [--field image|runtime|tool|provider|model|hosting]
```

- `set` is a **partial update** — pass only the flags you want to change.
- `clear` without `--field` strips the whole block; `clear --field X` clears one field only.
- `--hosting` is agent-exclusive (never inherits from the unit).
- `--provider` / `--model` are meaningful only when `--tool dapr-agent` (#598 gating). The portal hides them for other tool selections; the CLI accepts them unconditionally but they're ignored at dispatch for non-`dapr-agent` launchers.

`spring agent create` also accepts `--image`, `--runtime`, `--tool` as convenience shorthands for the equivalent `execution.X` fields — they overlay onto any `--definition` / `--definition-file` JSON body (last-writer-wins per field). Closes the #409 acceptance criterion for CLI parity.

```
spring agent create backend-eng --tool claude-code --image ghcr.io/my/agent:v1 --runtime podman
```

The legacy shorthand below still exists for a handful of older flags and will be folded into the `policy` verb group over time:

```
spring unit set <name> \
  --policy communication=hybrid \
  --policy work-assignment=unit-assigns \
  --policy initiative.max-level=proactive
```

**Communication policies:**
- `through-unit` -- all messages pass through the unit's orchestration
- `peer-to-peer` -- agents message each other directly
- `hybrid` -- combination of both

**Work assignment policies:**
- `unit-assigns` -- the unit's orchestration decides who does what
- `self-select` -- agents choose work themselves
- `capability-match` -- automatic matching by expertise

### Managing Members

```
spring unit members add <unit> --agent <agent> [--model …] [--specialty …] [--enabled …] [--execution-mode …]
spring unit members add <unit> --unit <child>
spring unit members remove <unit> --agent <agent>
spring unit members remove <unit> --unit <child>
spring unit members list <unit>
```

`--agent` and `--unit` are mutually exclusive on both `add` and `remove`; supply exactly one. The `--agent` shape on `add` accepts the per-membership overrides above (`--model` / `--specialty` / `--enabled` / `--execution-mode`); the `--unit` shape carries no per-edge config today (#217). On `remove`, the `--unit` path detaches a sub-unit edge — the server keeps the parent-required invariant (#744), so removing the last parent of a non-top-level child returns 409 with the offending unit address surfaced in the CLI's error output.

`unit members list --output json` returns one row per member with a unified `member` field carrying the scheme-prefixed canonical address (`agent://<path>` for agent members, `unit://<path>` for sub-units), so scripts can read the member id without branching on `agentAddress` vs `subUnitId`. The HTTP `/api/v1/units/{id}/memberships` and `/api/v1/agents/{id}/memberships` surfaces carry the same field on `UnitMembershipResponse`.

### Managing Humans

```
spring unit humans add <unit> <identity> --permission owner|operator|viewer [--identity <display>] [--notifications slack,email]
spring unit humans remove <unit> <identity>
spring unit humans list <unit>
```

`add` and `remove` require an `owner` on the target unit; `list` requires at least a `viewer`. `remove` is idempotent — calling it twice in a row succeeds both times. The `--notifications` flag accepts either `true`/`false` or a comma-separated channel list; any non-empty list enables notifications while `false` / `none` disables them.

### Starting and Stopping

```
spring unit start <unit>
spring unit stop <unit>
```

### Deleting a Unit

```
spring unit delete <unit>
```

Stops all agents, deactivates actors, cleans up subscriptions and execution environments. Agent state and activity history are retained (soft delete) for audit.

### Exporting a Unit

Capture the current state as declarative YAML:

```
spring unit export <unit> > engineering-team.yaml
```

This works regardless of how the unit was originally built (imperatively or declaratively).

## Agent Lifecycle

### Creating an Agent

```
spring agent create <id> \
  --role <role> \
  --capabilities "<comma-separated>" \
  --ai-backend <provider> \
  --tool <tool-name>
```

### Viewing Agent Status

```
spring agent status <agent>
spring agent status --unit <unit>    # all agents in a unit
```

### Configuring Agent Details

Agent instructions, expertise, and other properties are typically set via YAML definitions. For quick adjustments:

```
spring agent set <agent> --instructions "You are a backend engineer..."
```

### Agent Cloning Configuration

Cloning policies are set in the agent definition (YAML) or via the CLI:

```
spring agent set <agent> \
  --cloning-policy ephemeral-with-memory \
  --cloning-attachment attached \
  --cloning-max 3
```

### Creating and Listing Clones

Mirror the portal's Create Clone action from the CLI. The server assigns the
clone id; `--name` is an optional local alias the CLI echoes back for
scripts that need to tag a clone during provisioning.

```
# Create a clone with the portal's default policy (ephemeral-no-memory, detached).
spring agent clone create --agent ada

# Override the defaults.
spring agent clone create --agent ada \
  --clone-type ephemeral-with-memory \
  --attachment-mode attached \
  --name ada-review-clone

# List every clone of an agent.
spring agent clone list --agent ada
```

### Persistent Cloning Policy

The enum in the agent definition tells the workflow *how* to build a single clone. A **persistent cloning policy** (#416) is a separate governance record the platform enforces on *every* clone request — per-agent or tenant-wide. It controls:

- which memory-shape policies the caller may request (`allowed-policy`),
- which attachment modes are permitted (`allowed-attachment`),
- how many concurrent clones are allowed (`max-clones`),
- how deeply a clone may be cloned (`max-depth` — `0` disables recursive cloning at this scope),
- a per-clone cost budget.

Numeric caps collapse to the tightest non-null value across agent + tenant scope, so a tenant ceiling cannot be relaxed by an agent-scoped override. The enforcer also refuses detached clones for agents sitting behind an opaque unit boundary so the boundary is never silently crossed.

```
# Look up the effective agent-scoped policy (empty shape when none is set).
spring agent clone policy get ada

# Pin the policy: only ephemeral-with-memory clones, attached, max 3, depth 1.
spring agent clone policy set ada \
  --allowed-policy ephemeral-with-memory \
  --allowed-attachment attached \
  --max-clones 3 \
  --max-depth 1

# Remove the record (reverts to tenant-scoped / unconstrained).
spring agent clone policy clear ada

# Tenant-wide defaults — applied when an agent has no agent-scoped row.
spring agent clone policy set --scope tenant --max-clones 20 --max-depth 2
```

HTTP operators can PUT the same shape against `/api/v1/agents/{id}/cloning-policy` or `/api/v1/tenant/cloning-policy`. A denied request returns HTTP 403 with an `deniedDimension` extension field so tooling can surface exactly which rule fired (`policy`, `attachment`, `max-clones`, `max-depth`, `budget`, or `boundary`).

## How an agent's container is launched

Every agent — ephemeral or persistent — runs through the same dispatch
path: the dispatcher resolves the agent definition, calls the matching
`IAgentToolLauncher.PrepareAsync` for an `AgentLaunchSpec`, starts a
container via the dispatcher service ([ADR 0012](../../decisions/0012-spring-dispatcher-service-extraction.md)),
polls `GET /.well-known/agent.json` on the in-container A2A endpoint
(default port `8999`), and sends the turn over A2A. The only branch is the
post-roundtrip lifecycle decision: `Ephemeral` tears the container down
on turn drain; `Persistent` leaves it registered for the next turn. See
[ADR 0025](../../decisions/0025-unified-agent-launch-contract.md) for the
unified-dispatch decision record and
[`docs/architecture/agent-runtime.md`](../../architecture/agent-runtime.md)
for the architecture deep-dive.

Three things every agent image has to do — the **BYOI conformance contract**
([ADR 0027](../../decisions/0027-agent-image-conformance-contract.md)):

1. Expose A2A 0.3.x at `http://0.0.0.0:8999/`.
2. Serve an Agent Card at `GET /.well-known/agent.json` whose
   `protocolVersion` is `"0.3"`.
3. Honour the launcher-supplied environment, including any `SPRING_*` keys
   the launcher stamps into `AgentLaunchSpec.EnvironmentVariables`. The
   most important one is `SPRING_AGENT_ARGV` — a **JSON-encoded array of
   strings** that the agent-base bridge `JSON.parse`s and execs as the
   spawned tool's argv on every `message/send`. The dispatcher never
   shell-splits argv strings ([#1063](https://github.com/cvoya-com/spring-voyage/issues/1063)).

There are three conformance paths — pick whichever fits your image
constraints:

| Path | When to pick it |
|------|-----------------|
| 1    | Default. `FROM ghcr.io/cvoya-com/agent-base:<semver>` and `RUN`-install your CLI tool. Works for anything that runs on Debian 12 + Node 22. |
| 2    | Non-Debian distro / non-default UID / Node-less image. Pull the bridge in via `npm i -g @cvoya/spring-voyage-agent-sidecar` (Node-bearing) or a SEA binary from each GitHub Release (Node-less). |
| 3    | Your image already speaks A2A natively (e.g. `dapr-agents`). No bridge involved. |

The Tier-A CLI launchers shipped with OSS (Claude Code, Codex, Gemini)
all use path 1. The Dapr Agent launcher uses path 3. See
[Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) for the step-by-step
recipes (with copy-pasteable Dockerfile snippets), the full env contract,
version compatibility rules, and debugging tips (where bridge logs go,
how to verify the readiness probe at `/.well-known/agent.json` and the
`/healthz` surface).

### Bundled reference images (what you can type into `execution.image` without a build of your own)

Spring Voyage ships three reference container images. Two of them are
**ready to dispatch** — you can paste them straight into a unit's or
agent's `execution.image` field (CLI, YAML manifest, or the portal's
**Execution** tab) once they exist on the host. The third is a base
image you `FROM` when authoring your own.

| Image reference | Conformance path | Pair with `tool:` | Pulled from a registry? | Ready to dispatch? |
|-----------------|------------------|-------------------|-------------------------|--------------------|
| `localhost/spring-voyage-agent-claude-code:latest` | path 1 (bridge) | `claude-code` | No — built locally by `./deployment/build-agent-images.sh`. | Yes, after that script runs. |
| `localhost/spring-voyage-agent-dapr:latest`        | path 3 (native A2A) | `dapr-agent` | No — built locally by `./deployment/build-agent-images.sh`. | Yes, after that script runs. |
| `ghcr.io/cvoya-com/agent-base:<semver>`             | path 1 base     | (none — no CLI inside) | Yes — pulled directly from GHCR. | No. The bridge has nothing to spawn on `message/send`; useful only as a `FROM` base or as a no-op smoke target with `SPRING_AGENT_ARGV='["true"]'`. |

So the practical answer to "which images can I enter when creating a
new unit, without building anything new?":

- The two `localhost/spring-voyage-agent-*:latest` images, **provided
  someone has already run `./deployment/build-agent-images.sh` on the
  dispatcher host** (they live in the local image store; the
  `localhost/` prefix is not a registry). `./deploy.sh build` runs that
  script for you.
- `ghcr.io/cvoya-com/agent-base:<semver>` pulls cleanly from GHCR
  without any build, but it has no agent CLI baked in, so a real turn
  against it will fail. Use it as a base in your own Dockerfile, not as
  a unit's image directly.

The same two-image table is reproduced in
[`README.md` § Custom agent images](../../../README.md#custom-agent-images)
and [`deployment/README.md` § Custom agent images](../../../deployment/README.md#custom-agent-images),
which also cover the `examples/dockerfiles/` starter templates for
layering extra tooling on top.

## Persistent Agents

Agents configured with `execution.hosting: persistent` run as long-lived
services instead of spinning a fresh container per turn. The CLI exposes a
lifecycle surface so operators can stand them up, inspect container health,
stream logs, and tear them down without deleting the underlying agent
record.

```
spring agent deploy   <id> [--image <image>] [--replicas 0|1]
spring agent undeploy <id>
spring agent scale    <id> --replicas 0|1
spring agent logs     <id> [--tail N]
spring agent status   <id>
spring agent delete   <id>   # removes the agent record; does NOT stop a running container
```

### Deploy

`spring agent deploy <id>` is idempotent — redeploying a healthy agent is a
no-op. When the agent is unhealthy the old container is stopped and a fresh
one is started. `--image` applies the override to this deployment only; the
stored `execution.image` on the agent definition is untouched, so the
override is useful for smoke-testing candidate images before rolling the
YAML.

### Undeploy vs delete

`undeploy` stops the container and drops the registry entry; the agent
record, memberships, and history survive, and a later `deploy` brings the
same agent back. `delete` removes the directory record — call `undeploy`
first, otherwise a dangling container survives.

### Scale

The OSS core supports `--replicas 0` (equivalent to `undeploy`) and
`--replicas 1` (equivalent to `deploy`) today. Values above 1 return a
clear error until horizontal scale / container pooling lands (see
[#362](https://github.com/cvoya-com/spring-voyage/issues/362)).

### Logs

`spring agent logs <id>` prints the tail of the container's combined
stdout+stderr. Pipe into `grep` or `less` as you would `docker logs`.
`--tail N` caps the number of lines (default: 200). When the agent is not
currently deployed the command exits with a clear "not deployed" error —
deploy first, then read.

### Status

`spring agent status <id>` renders the usual directory info plus, for a
persistent deployment, the container's running state, health, and id in
the same table. Use `--output json` to see the full deployment block
(image, endpoint, container id, started-at, consecutive failures).

## Connector Management

The `spring connector` verb family mirrors the web portal's connector chooser and unit Connector tab. Every verb reads from the same underlying service the portal uses, so the CLI and UI stay at parity.

### Listing Available Connector Types

```
spring connector catalog
spring connector catalog --output json
```

Lists every connector type the server has registered (slug, display name, description). This matches what the portal renders when you open a unit's Connector tab with no active binding.

### Showing a Unit's Current Binding

```
spring connector show --unit <unit>
spring connector show --unit <unit> --output json
```

Prints the unit's active binding pointer (`typeSlug`, `typeId`, typed `configUrl`, actions base URL). When the connector is GitHub, the command also pulls the typed config and renders owner / repo / events / installation id / reviewer in the same output. When the unit isn't bound to any connector, it prints `Unit '<unit>' has no active connector binding.` (or `{"unit":"<unit>","bound":false}` in JSON mode).

### Binding a Unit to a Connector

```
spring connector bind --unit <unit> --type github \
  --owner <owner> --repo <repo> \
  [--installation-id <id>] \
  [--events <event1> <event2> ...] \
  [--reviewer <github-login>]
```

Example:

```
spring connector bind --unit engineering-team --type github \
  --owner my-org --repo platform \
  --events issues pull_request issue_comment \
  --reviewer alice
```

`--reviewer` (added by [#1133](https://github.com/cvoya-com/spring-voyage/issues/1133)) is the GitHub login the connector requests as the default reviewer when this unit's agents open pull requests. It mirrors the portal's "Default reviewer" dropdown and is optional — agents that pass a reviewer explicitly per call still override it.

Bind writes the per-unit config and the connector binding atomically through the connector-owned PUT endpoint. GitHub is the only typed bind surface today; other connector types are surfaced in `catalog` but return a clear `not supported by 'spring connector bind' yet` message until their typed PUT lands. Removing a binding is still handled by the unit lifecycle (stop / delete); a dedicated `unbind` command will follow in a later PR.

### Listing Every Unit Bound to a Connector Type

```
spring connector bindings <slugOrId>
spring connector bindings <slugOrId> --output json
```

Prints the full list of units bound to a connector type — one row per unit, with the unit name, display name, and the connector's slug / type id. Mirrors the **Bound units** section of the portal's `/connectors/{slug}` page and rides the same single round-trip `GET /api/v1/connectors/{slugOrId}/bindings` endpoint (#520). An empty list prints `No units are currently bound to connector '<slugOrId>'.`; an unknown connector exits non-zero with a `not registered` message.

### Authenticating a Connector

Authentication is handled per-connector. For GitHub, operators install the GitHub App and supply the installation id on `bind`. Interactive auth flows will be added alongside the connectors that need them.

## Building Container Images

Packages include Dockerfiles for workflows and execution environments:

```
spring build packages/software-engineering                    # build all images
spring build packages/software-engineering/workflows          # workflows only
spring build packages/software-engineering/execution          # execution envs only
spring images list                                            # list built images
```

For local development, `spring apply` auto-builds missing images.

## See it in action

The end-to-end scenarios under [`tests/e2e/scenarios/`](../../../tests/e2e/scenarios) exercise every CRUD and lifecycle path in this guide. They double as reference examples — each script drives the real `spring` CLI against a running stack. See [`tests/e2e/README.md`](../../../tests/e2e/README.md) for the runner and prerequisites.

Scenarios most relevant to this guide:

- [`fast/02-create-unit-scratch.sh`](../../../tests/e2e/scenarios/fast/02-create-unit-scratch.sh) — minimal `spring unit create` + `spring unit list` round-trip (covered in "Unit Lifecycle" above).
- [`fast/03-create-unit-with-model.sh`](../../../tests/e2e/scenarios/fast/03-create-unit-with-model.sh) — create a unit with `--model` and `--color` overrides and assert the response carries them. This is the path that exercises actor metadata wiring.
- [`fast/04-create-unit-from-template.sh`](../../../tests/e2e/scenarios/fast/04-create-unit-from-template.sh) — template-based creation with three-way cross-verification between CLI, `/memberships`, and `/agents` read paths.
- [`fast/06-unit-membership-roundtrip.sh`](../../../tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh) — full membership CRUD: `spring unit members add` with `--model`/`--specialty`/`--enabled`/`--execution-mode`, `members config` (upsert), `members remove`, and `unit purge --confirm` (which refuses without `--confirm`). Matches every section under "Managing Members" above.
- [`fast/07-create-start-unit.sh`](../../../tests/e2e/scenarios/fast/07-create-start-unit.sh) — `spring unit start` + status polling, matching "Starting and Stopping".
- [`fast/12-nested-units.sh`](../../../tests/e2e/scenarios/fast/12-nested-units.sh) — nested units via `spring unit members add <parent> --unit <child>`, with verification that the sub-unit appears in both the actor's status payload and the CLI's joined member list.
- [`fast/15-unit-policy-roundtrip.sh`](../../../tests/e2e/scenarios/fast/15-unit-policy-roundtrip.sh) — `GET`/`PUT /api/v1/units/{id}/policy` CRUD for the `skill` and `model` dimensions, plus 404 on unknown unit — the read/write path every policy-editing surface depends on.
- [`llm/30-policy-block-at-turn-time.sh`](../../../tests/e2e/scenarios/llm/30-policy-block-at-turn-time.sh) — (requires Ollama) unit + agent + policy + turn dispatch, the wiring proof that `spring message send` surfaces denials at turn time.
- [`llm/40-dapr-agent-turn.sh`](../../../tests/e2e/scenarios/llm/40-dapr-agent-turn.sh) — (requires Ollama) create an agent with `--tool dapr-agent` and dispatch a turn through the A2A protocol, proving the DaprAgentLauncher + container path end-to-end.
