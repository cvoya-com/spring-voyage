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
spring unit members add <unit> <agent-or-unit>
spring unit members remove <unit> <agent-or-unit>
spring unit members list <unit>
```

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

Prints the unit's active binding pointer (`typeSlug`, `typeId`, typed `configUrl`, actions base URL). When the connector is GitHub, the command also pulls the typed config and renders owner / repo / events / installation id in the same output. When the unit isn't bound to any connector, it prints `Unit '<unit>' has no active connector binding.` (or `{"unit":"<unit>","bound":false}` in JSON mode).

### Binding a Unit to a Connector

```
spring connector bind --unit <unit> --type github \
  --owner <owner> --repo <repo> \
  [--installation-id <id>] \
  [--events <event1> <event2> ...]
```

Example:

```
spring connector bind --unit engineering-team --type github \
  --owner my-org --repo platform \
  --events issues pull_request issue_comment
```

Bind writes the per-unit config and the connector binding atomically through the connector-owned PUT endpoint. GitHub is the only typed bind surface today; other connector types are surfaced in `catalog` but return a clear `not supported by 'spring connector bind' yet` message until their typed PUT lands. Removing a binding is still handled by the unit lifecycle (stop / delete); a dedicated `unbind` command will follow in a later PR.

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

The end-to-end scenarios under [`tests/e2e/scenarios/`](../../tests/e2e/scenarios/) exercise every CRUD and lifecycle path in this guide. They double as reference examples — each script drives the real `spring` CLI against a running stack. See [`tests/e2e/README.md`](../../tests/e2e/README.md) for the runner and prerequisites.

Scenarios most relevant to this guide:

- [`fast/02-create-unit-scratch.sh`](../../tests/e2e/scenarios/fast/02-create-unit-scratch.sh) — minimal `spring unit create` + `spring unit list` round-trip (covered in "Unit Lifecycle" above).
- [`fast/03-create-unit-with-model.sh`](../../tests/e2e/scenarios/fast/03-create-unit-with-model.sh) — create a unit with `--model` and `--color` overrides and assert the response carries them. This is the path that exercises actor metadata wiring.
- [`fast/04-create-unit-from-template.sh`](../../tests/e2e/scenarios/fast/04-create-unit-from-template.sh) — template-based creation with three-way cross-verification between CLI, `/memberships`, and `/agents` read paths.
- [`fast/06-unit-membership-roundtrip.sh`](../../tests/e2e/scenarios/fast/06-unit-membership-roundtrip.sh) — full membership CRUD: `spring unit members add` with `--model`/`--specialty`/`--enabled`/`--execution-mode`, `members config` (upsert), `members remove`, and `unit purge --confirm` (which refuses without `--confirm`). Matches every section under "Managing Members" above.
- [`fast/07-create-start-unit.sh`](../../tests/e2e/scenarios/fast/07-create-start-unit.sh) — `spring unit start` + status polling, matching "Starting and Stopping".
- [`fast/12-nested-units.sh`](../../tests/e2e/scenarios/fast/12-nested-units.sh) — nested units via `spring unit members add <parent> --unit <child>`, with verification that the sub-unit appears in both the actor's status payload and the CLI's joined member list.
- [`fast/15-unit-policy-roundtrip.sh`](../../tests/e2e/scenarios/fast/15-unit-policy-roundtrip.sh) — `GET`/`PUT /api/v1/units/{id}/policy` CRUD for the `skill` and `model` dimensions, plus 404 on unknown unit — the read/write path every policy-editing surface depends on.
- [`llm/30-policy-block-at-turn-time.sh`](../../tests/e2e/scenarios/llm/30-policy-block-at-turn-time.sh) — (requires Ollama) unit + agent + policy + turn dispatch, the wiring proof that `spring message send` surfaces denials at turn time.
- [`llm/40-dapr-agent-turn.sh`](../../tests/e2e/scenarios/llm/40-dapr-agent-turn.sh) — (requires Ollama) create an agent with `--tool dapr-agent` and dispatch a turn through the A2A protocol, proving the DaprAgentLauncher + container path end-to-end.
