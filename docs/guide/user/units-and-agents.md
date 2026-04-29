# Managing Units and Agents

This guide covers the full lifecycle of units and agents: creation, configuration, membership management, policies, and teardown. See [Web Portal Walkthrough](portal.md) for the equivalent portal flows.

## Unit Lifecycle

### Creating a Unit

```
spring unit create <name> [--description "..."]
```

A unit is usable immediately after creation. You can add agents, connectors, and policies incrementally.

#### From a template

```
spring unit create-from-template <package>/<template-name> [--name <override>] [--display <display-name>]
```

`--name` overrides the manifest-derived unit name so repeated instantiations don't collide. The legacy `spring unit create --from-template <package>/<template>` flag still works but prints a deprecation notice.

### Listing Units

```
spring unit list
```

### Configuring a Unit

Set execution defaults (image, runtime, tool, provider, model) and orchestration strategy independently:

```bash
# Set one or more execution defaults (partial update — pass only flags you want to change)
spring unit execution set <name> \
  --tool claude-code \
  --image localhost/spring-voyage-agent-claude-code:latest \
  --runtime podman \
  --model claude-sonnet-4-6

# Set orchestration strategy
spring unit orchestration set <name> --strategy ai
```

There is no `spring unit set` verb. Execution defaults and orchestration are separate verb groups (`execution` and `orchestration`). Use `spring unit execution get <name>` to inspect current defaults and `spring unit execution clear <name>` to strip the block.

### Setting Policies

Per-unit governance policies (skill, model, cost, execution mode, initiative) use the unified `spring unit policy` verb group:

```bash
spring unit policy skill          get|set|clear <unit> [flags...]
spring unit policy model          get|set|clear <unit> [flags...]
spring unit policy cost           get|set|clear <unit> [flags...]
spring unit policy execution-mode get|set|clear <unit> [flags...]
spring unit policy initiative     get|set|clear <unit> [flags...]
```

```bash
spring unit policy skill set eng-team --allowed github,filesystem --blocked shell
spring unit policy model set eng-team --allowed claude-sonnet-4,gpt-4o --blocked gpt-3.5-turbo
spring unit policy cost set eng-team --max-per-invocation 0.50 --max-per-hour 5 --max-per-day 25
spring unit policy execution-mode set eng-team --forced OnDemand
spring unit policy initiative set eng-team --max-level Proactive --blocked agent.spawn
```

Pass a YAML fragment instead of flags: `spring unit policy skill set eng-team -f skill-policy.yaml`

`get` prints the current slot plus the inheritance chain; `clear` removes one dimension without touching the others.

### Orchestration Strategy

```bash
spring unit orchestration get   <unit>
spring unit orchestration set   <unit> --strategy {ai|workflow|label-routed} [--label-routing <file>]
spring unit orchestration clear <unit>
```

- `set` writes the `orchestration.strategy` slot — same as `spring apply -f unit.yaml`, without a full re-apply.
- `set --label-routing <file>` also applies a `UnitPolicy.LabelRouting` YAML fragment.
- `clear` removes the slot; the resolver falls back to `UnitPolicy.LabelRouting`-inferred `label-routed` when set, otherwise the platform default ([ADR-0010](../../decisions/0010-manifest-orchestration-strategy-selector.md)).

Writes invalidate the in-process resolver cache immediately.

### Execution defaults

Units and agents share a five-field `execution:` block (`image`, `runtime`, `tool`, `provider`, `model`). The unit block acts as the default inherited by member agents. See `docs/architecture/units.md § Unit execution defaults` for the resolution chain.

```bash
spring unit execution get   <unit>
spring unit execution set   <unit> [--image …] [--runtime docker|podman] [--tool …] [--provider …] [--model …]
spring unit execution clear <unit> [--field image|runtime|tool|provider|model]

spring agent execution get   <agent>
spring agent execution set   <agent> [--image …] [--runtime …] [--tool …] [--provider …] [--model …] [--hosting ephemeral|persistent]
spring agent execution clear <agent> [--field image|runtime|tool|provider|model|hosting]
```

- `set` is a **partial update** — pass only the flags to change.
- `clear --field X` clears one field; `clear` without `--field` strips the whole block.
- `--hosting` is agent-exclusive.
- `--provider` / `--model` are meaningful only when `--tool dapr-agent`.

`spring agent create` accepts `--image`, `--runtime`, `--tool` as shorthands for the corresponding `execution.X` fields:

```bash
spring agent create backend-eng --tool claude-code --image ghcr.io/my/agent:v1 --runtime podman
```

### Managing Members

```bash
spring unit members add <unit> --agent <agent> [--model …] [--specialty …] [--enabled …] [--execution-mode …]
spring unit members add <unit> --unit <child>
spring unit members remove <unit> --agent <agent>
spring unit members remove <unit> --unit <child>
spring unit members list <unit>
```

`--agent` and `--unit` are mutually exclusive; supply exactly one. Removing the last parent of a non-top-level child returns 409. `--output json` returns a unified `member` field with the scheme-prefixed canonical address (`agent://<path>` or `unit://<path>`).

### Managing Humans

```bash
spring unit humans add <unit> <identity> --permission owner|operator|viewer [--notifications slack,email]
spring unit humans remove <unit> <identity>
spring unit humans list <unit>
```

`add` and `remove` require `owner` permission; `list` requires `viewer`. `remove` is idempotent. `--notifications` accepts `true`/`false` or a comma-separated channel list.

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

```bash
spring agent create <id> --role <role> --tool <tool-name>
```

Agent instructions, expertise, and other properties are typically set via YAML definitions. For quick adjustments:

```bash
spring agent set <agent> --instructions "You are a backend engineer..."
```

### Viewing Agent Status

```bash
spring agent status <agent>
spring agent status --unit <unit>    # all agents in a unit
```

### Agent Cloning Configuration

```bash
spring agent set <agent> \
  --cloning-policy ephemeral-with-memory \
  --cloning-attachment attached \
  --cloning-max 3
```

### Creating and Listing Clones

```bash
# Create a clone (ephemeral-no-memory, detached by default)
spring agent clone create --agent ada

# Override defaults
spring agent clone create --agent ada \
  --clone-type ephemeral-with-memory \
  --attachment-mode attached \
  --name ada-review-clone

spring agent clone list --agent ada
```

### Persistent Cloning Policy

A persistent cloning policy is a per-agent (or tenant-wide) governance record that constrains every clone request: which memory-shape policies are allowed, which attachment modes, max concurrent clones, max clone depth, and per-clone cost budget. Numeric caps collapse to the tightest non-null value across agent + tenant scope.

```bash
spring agent clone policy get ada
spring agent clone policy set ada \
  --allowed-policy ephemeral-with-memory \
  --allowed-attachment attached \
  --max-clones 3 \
  --max-depth 1
spring agent clone policy clear ada

# Tenant-wide defaults
spring agent clone policy set --scope tenant --max-clones 20 --max-depth 2
```

A denied request returns HTTP 403 with a `deniedDimension` field naming the rule that fired.

## How an agent's container is launched

Every agent (ephemeral or persistent) goes through the same dispatch path: the dispatcher resolves the agent definition, calls `IAgentToolLauncher.PrepareAsync` for an `AgentLaunchSpec`, starts a container, polls `GET /.well-known/agent.json` on the A2A endpoint (default port `8999`), and sends the turn over A2A. After the turn: ephemeral containers are torn down; persistent ones remain registered. See [ADR 0025](../../decisions/0025-unified-agent-launch-contract.md) and [Architecture — Agent runtime](../../architecture/agent-runtime.md).

Every agent image must satisfy the **BYOI conformance contract** ([ADR 0027](../../decisions/0027-agent-image-conformance-contract.md)):

1. Expose A2A 0.3.x at `http://0.0.0.0:8999/`.
2. Serve an Agent Card at `GET /.well-known/agent.json` with `protocolVersion: "0.3"`.
3. Honour launcher-supplied `SPRING_*` environment variables, especially `SPRING_AGENT_ARGV` (a JSON-encoded argv array the bridge execs on `message/send`).

Three conformance paths:

| Path | When to use |
|------|-------------|
| 1 (default) | `FROM ghcr.io/cvoya-com/agent-base:<semver>` + install your CLI tool. Works on Debian 12 + Node 22. |
| 2 | Non-Debian / Node-less image — pull `@cvoya/spring-voyage-agent-sidecar` via npm or a SEA binary. |
| 3 | Image already speaks A2A natively (e.g. `dapr-agents`). No bridge involved. |

OSS launchers (Claude Code, Codex, Gemini) use path 1; Dapr Agent uses path 3. See [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) for recipes and debugging tips.

### Bundled reference images

| Image | Path | `tool:` | Ready to dispatch? |
|-------|------|---------|-------------------|
| `localhost/spring-voyage-agent-claude-code:latest` | 1 | `claude-code` | Yes — after `./deployment/build-agent-images.sh` runs |
| `localhost/spring-voyage-agent-dapr:latest` | 3 | `dapr-agent` | Yes — after `./deployment/build-agent-images.sh` runs |
| `ghcr.io/cvoya-com/agent-base:<semver>` | 1 base | (none) | No — use as a `FROM` base, not as a dispatch target |

`./deploy.sh build` runs `build-agent-images.sh` for you.

## Persistent Agents

Agents with `execution.hosting: persistent` run as long-lived services instead of spinning a fresh container per turn.

```bash
spring agent deploy   <id> [--image <image>] [--replicas 0|1]
spring agent undeploy <id>
spring agent scale    <id> --replicas 0|1
spring agent logs     <id> [--tail N]
spring agent status   <id>
spring agent delete   <id>   # removes agent record; does NOT stop a running container
```

- **deploy** is idempotent; redeploying a healthy agent is a no-op. `--image` overrides for this deployment only — useful for smoke-testing without changing the YAML.
- **undeploy** stops the container and drops the registry entry; the agent record and history survive.
- **delete** removes the directory record — call `undeploy` first to avoid a dangling container.
- **scale** supports `--replicas 0` (undeploy) and `--replicas 1` (deploy) today. Values above 1 return a clear error until horizontal scale lands ([#362](https://github.com/cvoya-com/spring-voyage/issues/362)).
- **logs** prints stdout+stderr tail (default 200 lines). Agent must be deployed.
- **status** shows directory info plus container state, health, and id for persistent agents. Use `--output json` for the full deployment block.

## Connector Management

```bash
spring connector catalog                     # list registered connector types
spring connector show --unit <unit>          # show a unit's active binding
spring connector bindings <slugOrId>         # list every unit bound to a connector type

spring connector bind --unit engineering-team --type github \
  --owner my-org --repo platform \
  --events issues pull_request issue_comment \
  --reviewer alice
```

- **catalog** lists slug, display name, and description for every registered connector type.
- **show** prints the binding pointer plus typed config (for GitHub: owner, repo, events, installation id, reviewer).
- **bind** writes the per-unit config and connector binding atomically. GitHub is the only typed bind surface today; other types show a "not yet supported" message. Removing a binding uses the unit lifecycle (stop / delete); a dedicated `unbind` command is planned.
- **bindings** lists every unit bound to a given connector type.

For GitHub, install the GitHub App and supply the installation id on `bind`. See [Register your GitHub App](github-app-setup.md).

## Building Container Images

```bash
spring build packages/software-engineering          # build all images
spring build packages/software-engineering/workflows  # workflows only
spring build packages/software-engineering/execution  # execution envs only
spring images list                                   # list built images
```

For local development `spring apply` auto-builds missing images.

## See it in action

The end-to-end scenarios under [`tests/e2e/scenarios/`](../../../tests/e2e/scenarios) exercise every CRUD and lifecycle path in this guide. See [`tests/e2e/README.md`](../../../tests/e2e/README.md) for the runner and prerequisites.

Key scenarios for this guide:

| Scenario | What it covers |
|----------|----------------|
| `fast/02-create-unit-scratch.sh` | `spring unit create` + `spring unit list` |
| `fast/04-create-unit-from-template.sh` | Template-based creation with CLI / API cross-verification |
| `fast/06-unit-membership-roundtrip.sh` | Full membership CRUD with overrides |
| `fast/07-create-start-unit.sh` | `spring unit start` + status polling |
| `fast/12-nested-units.sh` | Nested units via `spring unit members add --unit` |
| `fast/15-unit-policy-roundtrip.sh` | Policy CRUD for `skill` and `model` dimensions |
| `llm/30-policy-block-at-turn-time.sh` | Policy deny at turn dispatch (requires Ollama) |
| `llm/40-dapr-agent-turn.sh` | `dapr-agent` turn via A2A (requires Ollama) |
