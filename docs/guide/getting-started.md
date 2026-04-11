# Getting Started

This guide walks you through setting up Spring Voyage V2 and creating your first unit with agents.

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

Send a message to an agent:

```
spring message send agent://engineering-team/ada "Review the README and suggest improvements"
```

Watch the activity in real-time:

```
spring activity stream --unit engineering-team
```

Check agent status:

```
spring agent status --unit engineering-team
```

## What's Next

- [Managing Units and Agents](units-and-agents.md) -- detailed configuration, policies, and lifecycle operations
- [Messaging and Interaction](messaging.md) -- conversations, message routing, and interacting with agents
- [Observing Activity](observing.md) -- activity streams, cost tracking, and dashboards
- [Declarative Configuration](declarative.md) -- version-controlled YAML definitions
