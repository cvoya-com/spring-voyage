# Managing Units and Agents

This guide covers the full lifecycle of units and agents: creation, configuration, membership management, policies, and teardown. See [Web Portal Walkthrough](portal.md) for the equivalent portal flows.

## Unit Lifecycle

### Creating a Unit

```
spring unit create <name> [--description "..."] [--runtime <id>] [--model <id>]
```

A unit *is* an agent that has children — give it a runtime and model so its own
runtime can run when a message reaches it. A unit is usable immediately after
creation; add member agents, connectors, and policies incrementally.

#### From a package (catalog install)

```bash
spring package install <package-name> [--input key=value ...]
```

This installs all artefacts in the package atomically via `POST /api/v1/packages/install`. If any step fails, the whole install rolls back. The portal equivalent is the **From catalog** source on the `/units/create` wizard. Declarative YAML is shipped as a package and installed this way — there is no `spring apply` verb (see [ADR-0035](../../decisions/0035-package-as-bundling-unit.md)).

### Listing Units

```
spring unit list
```

### Configuring a Unit

Set execution defaults — `image`, `runtime`, `model-provider`, `model` — independently:

```bash
# Set one or more execution defaults (partial update — pass only flags you want to change)
spring unit execution set <name> \
  --runtime claude-code \
  --image ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest \
  --model claude-sonnet-4-6
```

Use `spring unit execution get <name>` to inspect current defaults and `spring unit execution clear <name> [--field <name>]` to strip the block or one field.

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

Units and agents share an `execution:` block — `image`, the agent `runtime`, and a structured `model` (`{provider, id}`). The unit block is the default inherited by member agents; agents add `hosting`. See [Units & agents — Execution config inheritance](../../architecture/units-and-agents.md#execution-config-inheritance) for the resolution chain.

```bash
spring unit execution get   <unit>
spring unit execution set   <unit> [--image …] [--runtime <id>] [--model-provider <id>] [--model <id>]
spring unit execution clear <unit> [--field image|runtime|model-provider|model]

spring agent execution get   <agent>
spring agent execution set   <agent> [--image …] [--runtime <id>] [--model-provider <id>] [--model <id>] [--hosting ephemeral|persistent]
spring agent execution clear <agent> [--field image|runtime|model-provider|model|hosting]
```

- `set` is a **partial update** — pass only the flags to change.
- `clear --field X` clears one field; `clear` without `--field` strips the whole block.
- `--hosting` is agent-exclusive.
- `--runtime` is the agent-runtime kind (`claude-code`, `codex`, `gemini`, `spring-voyage`). `--model-provider` is required for multi-provider runtimes (`spring-voyage`); the provider follows from the model otherwise. Container runtime (podman vs docker) is platform configuration, not an execution field.

`spring agent create` accepts `--image`, `--runtime`, `--model-provider`, `--model`, `--hosting` as shorthands for the corresponding `execution.X` fields:

```bash
spring agent create --name backend-eng --unit engineering-team --runtime claude-code --model claude-sonnet-4-6
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

## Equipping Skills

Skills are authored capabilities shipped inside packages (artefacts with
`kind: Skill`). Each skill carries a markdown prompt fragment plus an
optional `<name>.tools.json` declaration of the tools its prose expects
to invoke. Equipping a skill on a subject *concatenates that fragment*
into the assembled prompt: equipped on a **unit**, the body lands in
Layer 2 (unit context) and is visible to every member agent; equipped on
an **agent**, it lands in Layer 4 (agent instructions) and is private to
that agent. See [Units & agents — Prompt assembly](../../architecture/units-and-agents.md#prompt-assembly)
for the four-layer model and `docs/concepts/skills.md` for the package
authoring shape.

Addressing is always `<package>/<skill>`. No version pinning — operators
who reinstall the package with new content pick up the new body on the
next turn.

### Via the portal

Open the Unit (or Agent) detail page and switch to the **Skills** tab
(under `Config` on the agent overflow strip, surfaced top-level on the
unit). The tab shows:

- **Equipped list** — each row labelled `<pkg>/<skill>` with a short
  prompt summary, a tool-count chip when the skill declares any required
  tools, and a per-row Remove button (confirmation-gated).
- **Equip a skill** button — opens a focus-trapped dialog that lists
  every `kind: Skill` bundle across your installed packages. Type-ahead
  filters by package or skill name; clicking *Equip* writes the bundle
  to the subject's store and refreshes the equipped list in place.
- **Inherited rows (agent only)** — bundles equipped on the agent's
  owning unit render greyed-out with an `Inherited from <unit>` badge
  linking back to the parent unit's Skills tab. Operators detach an
  inherited skill by visiting that parent — agents cannot override an
  inherited bundle, only stack additional ones on top.

The dialog flags bundles already equipped (directly or inherited) with
an in-place `Equipped` pill so it is hard to double-equip.

### Via the CLI

```bash
spring unit  skills list   <unit>
spring unit  skills add    <unit>  --skill <package>/<skill>
spring unit  skills remove <unit>  --skill <package>/<skill>
spring unit  skills set    <unit>  --skill <package>/<skill>[,…]

spring agent skills list   <agent>
spring agent skills add    <agent> --skill <package>/<skill>
spring agent skills remove <agent> --skill <package>/<skill>
spring agent skills set    <agent> --skill <package>/<skill>[,…]
```

`add` is idempotent on `(packageName, skillName)`; `set` replaces the
full equipped list in one call. `list` shows the resolved bundles in
declaration order — the same order the assembled prompt renders them.

## Agent Lifecycle

### Creating an Agent

```bash
spring agent create --name <display-name> --unit <unit> --role <role> --runtime <runtime-id>
```

Agent identity is a server-allocated Guid; `--name` is the only display surface. `--unit` is repeatable and optional — omit it for a top-level tenant-parented agent.

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
| 3 | Image already speaks A2A natively (the `spring-voyage` agent image takes this path). No bridge involved. |

OSS launchers (Claude Code, Codex, Gemini) use path 1; the Spring Voyage Agent uses path 3. See [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) for recipes and debugging tips.

### Bundled reference images

| Image | Path | `runtime:` | Ready to dispatch? |
|-------|------|------------|-------------------|
| `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` | 1 | `claude-code` | Yes — after `./eng/build/build-agent-images.sh` runs |
| `ghcr.io/cvoya-com/spring-voyage-agent:latest` | 3 | `spring-voyage` | Yes — after `./eng/build/build-agent-images.sh` runs |
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
- **Connectors** — one group per bound connector that registers tools. Binding a connector to a unit auto-grants the whole namespace (`arxiv.*`, `websearch.*`, …) to that unit and to every agent in it; an inherited-from-unit badge appears on an agent's view when the grant flowed in from a parent unit. The GitHub connector binds the unit and ships container-side credentials but registers **no** `github.*` tools — agents use the in-container `gh` / `git` CLIs instead (see [Tools](../../concepts/tools.md)).
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
- **bind** writes the per-unit config and connector binding atomically. GitHub is the only typed bind surface today; other types show a "not yet supported" message. `spring connector unbind` removes a connector install from the tenant.
  - The `--reviewer` flag is **optional**. Omit it (or pass an empty value) to bind with no default reviewer — agents under the unit then open pull requests without a requested reviewer. See [Connectors § Authentication](../../concepts/connectors.md#authentication).
- **bindings** lists every unit bound to a given connector type.

For GitHub, install the GitHub App and supply the installation id on `bind`. See [Register your GitHub App](../operator/github-app-setup.md).

## Building Container Images

Agent and unit runtime images are built from the repo, not through the `spring`
CLI. The in-tree build scripts produce the OSS images:

```bash
./eng/build/build-agent-images.sh           # build the OSS agent images
./eng/build/build.sh                        # full build (also runs the above)
```

Operators bringing their own images follow [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md).

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
