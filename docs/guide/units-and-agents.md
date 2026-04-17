# Managing Units and Agents

This guide covers the full lifecycle of units and agents: creation, configuration, membership management, policies, and teardown.

## Unit Lifecycle

### Creating a Unit

```
spring unit create <name> [--description "..."]
```

A unit is usable immediately after creation. You can add agents, connectors, and policies incrementally.

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
spring unit humans add <unit> <identity> --permission owner|operator|viewer
spring unit humans remove <unit> <identity>
spring unit humans list <unit>
```

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

## Connector Management

### Adding a Connector

```
spring connector add <type> --unit <unit> [--config key=value ...]
```

Example:
```
spring connector add github --unit engineering-team --repo savasp/spring
```

### Authenticating a Connector

```
spring connector auth <type> --unit <unit>
```

Opens an OAuth flow or prompts for a token.

### Listing Connectors

```
spring connector list --unit <unit>
```

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
- [`llm/30-policy-block-at-turn-time.sh`](../../tests/e2e/scenarios/llm/30-policy-block-at-turn-time.sh) — (requires Ollama) unit + agent + policy + turn dispatch, the wiring proof that `spring message send` surfaces denials at turn time.
- [`llm/40-dapr-agent-turn.sh`](../../tests/e2e/scenarios/llm/40-dapr-agent-turn.sh) — (requires Ollama) create an agent with `--tool dapr-agent` and dispatch a turn through the A2A protocol, proving the DaprAgentLauncher + container path end-to-end.
