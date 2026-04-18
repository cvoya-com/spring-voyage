# Declarative Configuration

This guide covers how to define units and agents in YAML files and apply them with `spring apply`. Declarative configuration is the recommended approach for reproducible, version-controlled setups.

## Two Paths to the Same Result

Spring Voyage supports both imperative (CLI commands) and declarative (YAML files) configuration. Both produce the same actor state. Use imperative for exploration and prototyping; declarative for reproducibility and version control.

## Agent Definition Files

Agent definitions describe what the agent is:

```yaml
# agents/ada.yaml
agent:
  id: ada
  name: Ada
  role: backend-engineer
  capabilities: [csharp, python, postgresql, testing]

  ai:
    agent: claude
    model: claude-sonnet-4-20250514
    execution: delegated
    tool: claude-code
    environment:
      image: spring-agent:latest
      runtime: podman

  cloning:
    policy: ephemeral-with-memory
    attachment: attached
    max_clones: 3

  instructions: |
    You are a backend engineer specializing in C# and Python.
    You write clean, well-tested code with clear documentation.

  expertise:
    - domain: python/fastapi
      level: advanced
    - domain: postgresql
      level: intermediate

  activations:
    - type: message
    - type: subscription
      topic: pr-reviews
      filter: "labels contains 'backend'"
    - type: reminder
      schedule: "0 9 * * MON-FRI"
      payload: { action: "daily-standup" }
```

## Unit Definition Files

Unit definitions describe the group and its configuration:

```yaml
# units/engineering-team.yaml
unit:
  name: engineering-team
  description: Software engineering team
  structure: hierarchical

  ai:
    execution: delegated
    tool: software-dev-cycle
    environment:
      image: spring-workflows/software-dev-cycle:latest
      runtime: podman

  members:
    - agent: agents/ada.yaml
    - agent: agents/kay.yaml
    - agent: agents/hopper.yaml
    - unit: units/database-team.yaml    # nested unit

  execution:
    image: spring-agent:latest
    runtime: podman

  connectors:
    - type: github
      config:
        repo: savasp/spring
        webhook_secret: ${GITHUB_WEBHOOK_SECRET}
    - type: slack
      config:
        channel: "#engineering-team"

  policies:
    communication: hybrid
    work_assignment: unit-assigns
    initiative:
      max_level: proactive

  humans:
    - identity: savasp
      permission: owner
      notifications: [slack, email]
    - identity: reviewer2
      permission: operator
      notifications: [github]

  # Optional boundary — controls what members expose to callers outside
  # the unit. Wire-equivalent to `spring unit boundary set -f`; see
  # docs/architecture/units.md § Unit Boundary for the rule semantics.
  boundary:
    opacities:
      - domain_pattern: internal-*
    projections:
      - domain_pattern: backend-*
        rename_to: engineering
        override_level: advanced
    syntheses:
      - name: full-stack
        description: team-level full-stack coverage
        level: expert
```

Members can reference agent/unit definition files (relative paths) or existing agents/units by ID.

## Applying Definitions

### Apply a Single File

```
spring apply -f units/engineering-team.yaml
```

This validates all definitions, creates actors, registers subscriptions, initializes connectors, and reports status.

### Re-Applying

Re-applying performs a diff and applies changes incrementally -- no teardown required. If you add a new agent to the members list, only that agent is created. If you change a policy, only the policy is updated.

```
# Edit the YAML, then re-apply
spring apply -f units/engineering-team.yaml
```

### Validation

Check a definition for errors without applying:

```
spring validate -f units/engineering-team.yaml
```

## Environment Variables

Use `${VAR_NAME}` syntax for secrets and environment-specific values:

```yaml
connectors:
  - type: github
    config:
      webhook_secret: ${GITHUB_WEBHOOK_SECRET}
```

Values are resolved from environment variables at apply time.

## Exporting to YAML

Capture any unit's current state as declarative YAML:

```
spring unit export engineering-team > engineering-team.yaml
```

This works regardless of how the unit was originally created. Useful for:

- Version-controlling an imperatively-built unit
- Creating templates from working configurations
- Migrating configurations between environments

## Recommended Workflow

1. **Prototype** imperatively with CLI commands
2. **Export** when the configuration works: `spring unit export <name> > unit.yaml`
3. **Version control** the YAML files
4. **Apply** from YAML for all environments: `spring apply -f unit.yaml`
5. **Iterate** by editing YAML and re-applying

## Building Referenced Images

If YAML files reference container images that don't exist locally, `spring apply` auto-builds them from package Dockerfiles. For production, pre-build images:

```
spring build packages/software-engineering
```
