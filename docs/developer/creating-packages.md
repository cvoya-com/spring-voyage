# Creating Packages

This guide covers how to create domain packages -- bundles of agents, skills, workflows, connectors, and execution environments that bring domain expertise to the platform.

## Package Structure

A package is a directory following this convention:

```
packages/<domain-name>/
  agents/                    # Agent definition YAML files
  units/                     # Unit definition YAML files
  skills/                    # Prompt fragments + tool definitions
  workflows/                 # Workflow container sources
    <workflow-name>/
      Dockerfile
      <ProjectDir>/          # .NET project, Python code, etc.
  execution/                 # Agent execution environment sources
    <env-name>/
      Dockerfile
  connectors/                # Connector source (compiled into host)
    <connector-name>/
```

## Creating Agent Templates

Agent YAML files define reusable agent configurations:

```yaml
# packages/my-domain/agents/researcher.yaml
agent:
  id: researcher
  name: Researcher
  role: researcher
  capabilities: [analysis, summarization, literature-review]

  ai:
    agent: claude
    model: claude-sonnet-4-6
    tool: claude-code

  instructions: |
    You are a research analyst. You analyze papers,
    summarize findings, and identify patterns.

  expertise:
    - domain: machine-learning
      level: advanced
    - domain: statistics
      level: intermediate

  activations:
    - type: message
    - type: subscription
      topic: new-papers
```

## Creating Skills

Skills are the smallest reusable unit -- a prompt fragment and optional tool definitions.

### Prompt Fragment

```markdown
<!-- packages/my-domain/skills/paper-analysis.md -->
## Paper Analysis

When you receive a research paper:
1. Read the abstract and introduction first
2. Identify the key contribution and methodology
3. Assess the strength of evidence
4. Note any limitations or concerns
5. Summarize in 2-3 paragraphs with your assessment
```

### Tool Definitions (Optional)

```json
// packages/my-domain/skills/paper-analysis.tools.json
[
  {
    "name": "classifyPaper",
    "description": "Classify a paper by topic, methodology, and quality",
    "parameters": {
      "type": "object",
      "required": ["paperId", "topics", "methodology"],
      "properties": {
        "paperId": { "type": "string" },
        "topics": { "type": "array", "items": { "type": "string" } },
        "methodology": { "type": "string", "enum": ["experimental", "theoretical", "survey", "mixed"] },
        "qualityScore": { "type": "number", "minimum": 1, "maximum": 5 }
      }
    }
  }
]
```

### Composing Skills

Skills are referenced in unit or agent definitions:

```yaml
ai:
  skills:
    - package: my-org/my-domain
      skill: paper-analysis
    - package: my-org/my-domain
      skill: literature-review
```

Prompt fragments are concatenated in declaration order. Tool definitions are merged.

## Creating Workflow Containers

Domain workflows run as containers. Create a Dockerfile and the orchestration code:

```
packages/my-domain/workflows/research-cycle/
  Dockerfile
  ResearchCycle/
    ResearchCycle.csproj
    ResearchCycleWorkflow.cs
```

The workflow communicates with agents via its Dapr sidecar. It can use Dapr Workflows (C# or Python), Temporal, or any custom process.

### Referencing in Unit Definitions

```yaml
unit:
  ai:
    execution: delegated
    tool: research-cycle
    environment:
      image: my-org/research-cycle:latest
      runtime: podman
```

## Creating Execution Environments

Execution environments are containers where delegated agents do work:

```
packages/my-domain/execution/research-env/
  Dockerfile
```

The Dockerfile sets up the tools the agent needs -- Claude Code, Python packages, data analysis tools, etc.

### Referencing in Agent/Unit Definitions

Agent-level (specific to this agent):
```yaml
agent:
  ai:
    environment:
      image: my-org/research-env:latest
      runtime: podman
```

Unit-level (default for all members):
```yaml
unit:
  execution:
    image: my-org/research-env:latest
    runtime: podman
```

Agents that don't specify their own environment inherit the unit's default.

## Creating Connectors

### Simple Connectors (Dapr Bindings)

For straightforward integrations, create a Dapr binding YAML:

```yaml
# dapr/components/my-binding.yaml
apiVersion: dapr.io/v1alpha1
kind: Component
metadata:
  name: my-webhook
spec:
  type: bindings.http
  metadata:
    - name: url
      value: "https://api.example.com/webhook"
```

### Rich Connectors (Custom Actors)

For bidirectional, stateful integrations, create a .NET project:

```
packages/my-domain/connectors/my-service/
  Spring.Connector.MyService.csproj
  MyServiceConnectorActor.cs
  MyServiceEventTranslator.cs
  MyServiceSkills.cs
```

Rich connectors implement the connector interface, translate external events to platform messages, and provide skills for agents.

## Building and Testing

```
# Build container images
spring build packages/my-domain

# Apply the package
spring apply -f packages/my-domain/units/research-cell.yaml

# Test with a message — resolve the unit's id, then send to it
spring unit show research-cell                       # prints the canonical Guid
spring message send unit:<id> "Analyze this paper: ..."
```

## Phase 6: Formal Package Distribution

In Phase 6, packages gain formal distribution:

```
# Package and publish
spring package publish my-org/my-domain --version 1.0.0

# Install from registry
spring package install my-org/my-domain
```

Until then, packages are directories applied directly with `spring apply`.
