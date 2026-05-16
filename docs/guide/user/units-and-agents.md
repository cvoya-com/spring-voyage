# Managing Units and Agents

This guide covers the full lifecycle of units and agents: creation, configuration, membership management, policies, and teardown. See [Web Portal Walkthrough](portal.md) for the equivalent portal flows.

## Unit Lifecycle

### Creating a Unit

```
spring unit create <name> [--description "..."]
```

A unit is usable immediately after creation. You can add agents, connectors, and policies incrementally.

#### From a package (catalog install)

```bash
spring package install <package-name> [--input key=value ...]
```

This installs all artefacts in the package atomically via `POST /api/v1/packages/install`. If any step fails, the whole install rolls back. The portal equivalent is the **From catalog** source on the `/units/create` wizard. The removed `spring unit create-from-template` and `spring unit create --from-template` verbs were superseded by this path (see [ADR-0035](../../decisions/0035-cross-package-self-contained.md)).

### Listing Units

```
spring unit list
```

### Configuring a Unit

Set execution defaults (image, runtime, tool, provider, model) independently:

```bash
# Set one or more execution defaults (partial update — pass only flags you want to change)
spring unit execution set <name> \
  --agent claude \
  --image ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest \
  --runtime podman \
  --model claude-sonnet-4-6
```

There is no `spring unit set` verb. Use `spring unit execution get <name>` to inspect current defaults and `spring unit execution clear <name>` to strip the block.

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

### Execution defaults

Units and agents share a five-field `execution:` block (`image`, `runtime`, `tool`, `provider`, `model`). The unit block acts as the default inherited by member agents. See `docs/architecture/units.md § Unit execution defaults` for the resolution chain.

```bash
spring unit execution get   <unit>
spring unit execution set   <unit> [--image …] [--runtime docker|podman] [--agent …] [--provider …] [--model …]
spring unit execution clear <unit> [--field image|runtime|tool|provider|model]

spring agent execution get   <agent>
spring agent execution set   <agent> [--image …] [--runtime …] [--agent …] [--provider …] [--model …] [--hosting ephemeral|persistent]
spring agent execution clear <agent> [--field image|runtime|tool|provider|model|hosting]
```

- `set` is a **partial update** — pass only the flags to change.
- `clear --field X` clears one field; `clear` without `--field` strips the whole block.
- `--hosting` is agent-exclusive.
- `--provider` / `--model` are meaningful only when the resolved runtime kind is `spring-voyage` (i.e. `--agent ollama|openai|google`).

`spring agent create` accepts `--image`, `--runtime`, `--agent` as shorthands for the corresponding `execution.X` fields:

```bash
spring agent create --name backend-eng --unit engineering-team --agent claude --image ghcr.io/my/agent:v1 --runtime podman
```

### Managing Members

```bash
spring unit members add <unit> --agent <agent> [--model …] [--specialty …] [--enabled …] [--execution-mode …]
spring unit members add <unit> --unit <child>
spring unit members remove <unit> --agent <agent>
spring unit members remove <unit> --unit <child>
spring unit members list <unit>
```

`--agent` and `--unit` are mutually exclusive; supply exactly one. Removing the last parent of a non-top-level child returns 409. `--output json` returns a unified `member` field with the scheme-prefixed canonical address (`agent:<32-hex>` or `unit:<32-hex>`).

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
spring agent create --name <display-name> --unit <unit> --role <role> --agent <runtime-id>
```

Agent identity is assigned by the platform (a server-allocated Guid) per ADR-0039 §8; `--name` is the only display surface.

Agent instructions, expertise, and other properties are typically set via YAML definitions. For quick adjustments:

```bash
# Replace the agent's instructions in place.
spring agent set <agent> --instructions "You are a backend engineer..."

# Read the new instructions from a file.
spring agent set <agent> --instructions @path/to/prompt.md

# Clear the slot (the agent then inherits from its parent unit).
spring agent set <agent> --instructions ""
```

The same verb shape works on a unit (members that have no own
`instructions` slot inherit the unit's value at dispatch):

```bash
spring unit set <unit> --instructions "Be helpful."
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

Every agent (ephemeral or persistent) goes through the same dispatch path: the dispatcher resolves the agent definition, calls `IAgentRuntimeLauncher.PrepareAsync` for an `AgentLaunchSpec`, starts a container, polls `GET /.well-known/agent.json` on the A2A endpoint (default port `8999`), and sends the turn over A2A. After the turn: ephemeral containers are torn down; persistent ones remain registered. See [ADR 0025](../../decisions/0025-unified-agent-launch-contract.md) and [Architecture — Agent runtime](../../architecture/agent-runtime.md).

Every agent image must satisfy the **BYOI conformance contract** ([ADR 0027](../../decisions/0027-agent-image-conformance-contract.md)):

1. Expose A2A 0.3.x at `http://0.0.0.0:8999/`.
2. Serve an Agent Card at `GET /.well-known/agent.json` with `protocolVersion: "0.3"`.
3. Honour launcher-supplied `SPRING_*` environment variables, especially `SPRING_AGENT_ARGV` (a JSON-encoded argv array the bridge execs on `message/send`).

Three conformance paths:

| Path | When to use |
|------|-------------|
| 1 (default) | `FROM ghcr.io/cvoya-com/spring-voyage-agent-base:<semver>` + install your CLI tool. Works on Debian 12 + Node 22. |
| 2 | Non-Debian / Node-less image — copy the bridge SEA binary from each GitHub Release into your custom base. |
| 3 | Image already speaks A2A natively (e.g. `dapr-agents`). No bridge involved. |

OSS launchers (Claude Code, Codex, Gemini) use path 1; Dapr Agent uses path 3. See [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) for recipes and debugging tips.

### Bundled reference images

| Image | Path | `tool:` | Ready to dispatch? |
|-------|------|---------|-------------------|
| `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` | 1 | `claude-code` | Yes — after `./eng/build/build-agent-images.sh` runs |
| `ghcr.io/cvoya-com/spring-voyage-agent:latest` | 3 | `spring-voyage-agent` | Yes — after `./eng/build/build-agent-images.sh` runs |
| `ghcr.io/cvoya-com/spring-voyage-agent-base:<semver>` | 1 base | (none) | No — use as a `FROM` base, not as a dispatch target |

`./eng/build/build.sh` runs `build-agent-images.sh` for you.

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

## Inspecting tools

Every unit and every agent has an **effective tool set** — the flat list of tools its runtime sees at dispatch. The portal surfaces it on the subject's **Config → Tools** sub-tab, split into three sections:

- **Platform** (collapsed) — every `sv.*` tool the runtime ships with. Implicit for every subject; no operator action grants or revokes it.
- **Connectors** — one group per bound connector. Binding a connector to a unit auto-grants the whole namespace (`github.*`, `arxiv.*`, …) to that unit and to every agent in it; an inherited-from-unit badge appears on an agent's view when the grant flowed in from a parent unit.
- **Image** — the tools the agent's container image declared at `GET /a2a/tools`. Read-only and 1:1 with the running image.

The same array is available on the `effectiveTools` field of every `AgentResponse` / `UnitResponse`. See [Tools](../../concepts/tools.md) for the model, the precedence rules, and how each tier reaches the subject.

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

The CLI scenarios under [`tests/e2e/cli/scenarios/`](../../../tests/e2e/cli/scenarios) exercise every CRUD and lifecycle path in this guide. See [`tests/e2e/cli/README.md`](../../../tests/e2e/cli/README.md) for the runner and prerequisites.

Key scenarios for this guide:

| Scenario | What it covers |
|----------|----------------|
| `units/unit-create-scratch.sh` | `spring unit create` + `spring unit list` |
| `units/unit-membership-roundtrip.sh` | Full membership CRUD with overrides |
| `units/unit-create-and-start.sh` | `spring unit start` + status polling |
| `units/unit-nested.sh` | Nested units via `spring unit members add --unit` |
| `policy/unit-policy-http-roundtrip.sh` | Policy CRUD for `skill` and `model` dimensions |
| `policy/policy-block-at-turn-time.sh` | Policy deny at turn dispatch (requires Ollama) |
| `agents/spring-voyage-agent-turn.sh` | `spring-voyage-agent` turn via A2A (requires Ollama) |
