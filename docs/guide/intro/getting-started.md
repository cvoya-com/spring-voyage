# Getting Started

This guide walks you through setting up Spring Voyage and creating your first unit with agents.

## Installation

Build the CLI from source (requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

```
git clone https://github.com/cvoya-com/spring-voyage.git
cd spring-voyage
dotnet build src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj

# Run the CLI directly
dotnet run --project src/Cvoya.Spring.Cli -- <command>

# Or publish a self-contained executable
dotnet publish src/Cvoya.Spring.Cli -c Release -o ./out
./out/spring <command>
```

The command name is `spring`.

## Authentication

If you're connecting to a hosted Spring Voyage platform, authenticate first:

```
spring auth
```

This drives the browser-based login flow and persists an API token to `~/.spring/config.json`. All subsequent commands are authenticated. You can also mint and manage tokens directly with `spring auth token create` / `list` / `revoke`.

A single-tenant OSS deployment seeds a default tenant on first boot; once authenticated against it, every command runs against that tenant.

## Creating Your First Unit

A unit *is* an agent that has children. Create one and give it a runtime — when
a message reaches a unit, the unit's own runtime runs and decides whether to
answer directly or hand work to a member:

```
spring unit create engineering-team --description "My engineering team" \
  --runtime claude-code
```

A unit does not declare a routing strategy: how a unit routes work across its
members is decided by the unit's own runtime, not by platform configuration
(see [ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md)).

### Set the Default Execution Environment

The unit's execution block is the default inherited by member agents. Set the
container image and other execution fields with `spring unit execution set`:

```
spring unit execution set engineering-team \
  --image ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
```

## Creating Agents

Create an agent and add it to the unit. Execution config (`--runtime`,
`--model`, `--image`, `--hosting`) is inherited from the parent unit when
omitted:

```
spring agent create \
  --name ada \
  --unit engineering-team \
  --role backend-engineer \
  --runtime claude-code \
  --model claude-sonnet-4-6
```

You can add more agents the same way:

```
spring agent create --name kay --unit engineering-team --role frontend-engineer --runtime claude-code
```

## Adding a Connector

Connectors bridge external systems. The GitHub connector is installed on the
tenant, then bound to a unit:

```
spring connector install github
spring connector bind --unit engineering-team --type github \
  --owner your-org --repo your-repo
```

GitHub authenticates as a GitHub App the deployment owns — see
[Register your GitHub App](../operator/github-app-setup.md).

## Adding Yourself as Owner

Grant yourself the owner permission on the unit, addressed by identity:

```
spring unit humans add engineering-team <your-identity> --permission owner
```

(`spring unit members humans add` is the parallel verb for adding a human as a
team-role *member* of the unit, distinct from this ACL grant.)

## Starting the Unit

```
spring unit start engineering-team
```

The unit and its agents are now active and ready to receive messages.

## Your First Interaction

Look up Ada's `Guid` (display-name search inside her unit):

```
spring agent show ada --unit engineering-team
# prints the canonical 32-hex Guid
```

Then send a message to that id:

```
spring message send agent:<id> "Review the README and suggest improvements"
```

Watch the activity in real-time:

```
spring activity tail --unit engineering-team
```

Check agent status:

```
spring agent status --unit engineering-team
```

## See it in action

Each step above has a matching end-to-end scenario you can read or run. Scenarios live under [`tests/e2e/cli/scenarios/`](../../../tests/e2e/cli/scenarios); see [`tests/e2e/cli/README.md`](../../../tests/e2e/cli/README.md) for prerequisites and the `./run.sh` runner.

- [`api/api-health.sh`](../../../tests/e2e/cli/scenarios/api/api-health.sh) — a raw smoke check that `/api/v1/connectors` responds. Useful for validating that the stack is up before anything else.
- [`cli-meta/cli-version-and-help.sh`](../../../tests/e2e/cli/scenarios/cli-meta/cli-version-and-help.sh) — verifies that `spring --help` starts cleanly and exposes the expected subcommands (`unit`, `agent`, `package`, …). Run this to confirm the CLI is wired correctly.
- [`units/unit-create-scratch.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-scratch.sh) — creates a minimal unit via `spring unit create` and asserts it shows up in `spring unit list`. This matches the "Creating Your First Unit" walkthrough above.
- [`units/unit-create-and-start.sh`](../../../tests/e2e/cli/scenarios/units/unit-create-and-start.sh) — creates a unit and transitions it to `Running` with `spring unit start`, mirroring "Starting the Unit" above.
- [`messaging/message-human-to-agent.sh`](../../../tests/e2e/cli/scenarios/messaging/message-human-to-agent.sh) — (`pool: llm`, requires Ollama) sends a human-authored message to an agent via `spring message send agent:<id>`, matching "Your First Interaction".

## What's Next

- [Managing Units and Agents](../user/units-and-agents.md) -- detailed configuration, policies, and lifecycle operations
- [Messaging and Interaction](../user/messaging.md) -- sending messages on threads and interacting with agents
- [Observing Activity](../user/observing.md) -- activity streams and cost tracking
- [Web Portal Walkthrough](../user/portal.md) -- the same operations from a browser
- [Declarative Configuration](../user/declarative.md) -- version-controlled YAML definitions
- [Runnable Examples](../user/examples.md) -- catalog of e2e scenarios you can study or execute
