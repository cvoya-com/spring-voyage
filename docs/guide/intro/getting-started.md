# Getting Started

This guide walks you through setting up Spring Voyage and creating your first unit with agents.

## Installation

Build the CLI from source (requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

```
git clone https://github.com/savasp/spring-voyage.git
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

### Remote Platform

If you're connecting to a hosted Spring Voyage platform, authenticate first:

```
spring auth
```

This opens the web portal in your browser. Log in (or create an account if it's your first time), and the CLI receives your session credential. All subsequent commands are authenticated.

### Local Development Mode

For local development, start the API Host in single-tenant mode:

```
spring --local
```

In this mode, authentication is disabled and all commands execute as an implicit local user. You'll need Podman (or Docker) and the Dapr CLI installed for local infrastructure.

## Creating Your First Unit

A unit is a group of agents. Start by creating one:

```
spring unit create engineering-team --description "My engineering team"
```

### Set the Orchestration Strategy

Tell the unit how to route messages to its members:

```
spring unit set engineering-team \
  --structure hierarchical \
  --ai-execution delegated \
  --ai-tool software-dev-cycle \
  --ai-environment-image spring-workflows/software-dev-cycle:latest \
  --ai-environment-runtime podman
```

### Set the Default Execution Environment

This is the container image that member agents will use by default:

```
spring unit set engineering-team \
  --execution-image spring-agent:latest \
  --execution-runtime podman
```

## Creating Agents

Create an agent and add it to the unit:

```
spring agent create ada \
  --role backend-engineer \
  --capabilities "csharp,python,postgresql" \
  --ai-backend claude \
  --execution delegated \
  --tool claude-code

spring unit members add engineering-team ada
```

You can add more agents the same way:

```
spring agent create kay --role frontend-engineer --capabilities "typescript,react" --ai-backend claude --execution delegated --tool claude-code
spring unit members add engineering-team kay
```

## Adding a Connector

Connectors bridge external systems. Add a GitHub connector:

```
spring connector add github --unit engineering-team --repo your-org/your-repo
spring connector auth github --unit engineering-team
```

The auth command opens an OAuth flow or prompts for a token.

## Adding Yourself as Owner

```
spring unit humans add engineering-team your-username --permission owner
```

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
spring activity stream --unit engineering-team
```

Check agent status:

```
spring agent status --unit engineering-team
```

## See it in action

Each step above has a matching end-to-end scenario you can read or run. Scenarios live under [`tests/cli-scenarios/scenarios/`](../../../tests/cli-scenarios/scenarios); see [`tests/cli-scenarios/README.md`](../../../tests/cli-scenarios/README.md) for prerequisites and the `./run.sh` runner.

- [`api/api-health.sh`](../../../tests/cli-scenarios/scenarios/api/api-health.sh) — a raw smoke check that `/api/v1/connectors` responds. Useful for validating that the stack is up before anything else.
- [`cli-meta/cli-version-and-help.sh`](../../../tests/cli-scenarios/scenarios/cli-meta/cli-version-and-help.sh) — verifies that `spring --help` starts cleanly and exposes the expected subcommands (`unit`, `apply`, …). Run this to confirm the CLI is wired correctly.
- [`units/unit-create-scratch.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-scratch.sh) — creates a minimal unit via `spring unit create` and asserts it shows up in `spring unit list`. This matches the "Creating Your First Unit" walkthrough above.
- [`units/unit-create-and-start.sh`](../../../tests/cli-scenarios/scenarios/units/unit-create-and-start.sh) — creates a unit and transitions it to `Running` with `spring unit start`, mirroring "Starting the Unit" above.
- [`messaging/message-human-to-agent.sh`](../../../tests/cli-scenarios/scenarios/messaging/message-human-to-agent.sh) — (`pool: llm`, requires Ollama) sends a human-authored message to an agent via `spring message send agent:<id>`, matching "Your First Interaction".

## What's Next

- [Managing Units and Agents](../user/units-and-agents.md) -- detailed configuration, policies, and lifecycle operations
- [Messaging and Interaction](../user/messaging.md) -- conversations, message routing, and interacting with agents
- [Observing Activity](../user/observing.md) -- activity streams, cost tracking, and dashboards
- [Web Portal Walkthrough](../user/portal.md) -- the same operations from a browser
- [Declarative Configuration](../user/declarative.md) -- version-controlled YAML definitions
- [Runnable Examples](../user/examples.md) -- catalog of e2e scenarios you can study or execute
