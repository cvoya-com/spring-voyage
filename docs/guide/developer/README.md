# Developer Guide

Building and running Spring Voyage from source.

For day-to-day operations once your local instance is up, follow the [Operator Guide](../operator/README.md) — running from source doesn't change the operator workflows.

For **extending** Spring Voyage (writing your own agent runtime, connector, skill bundle, or working on the platform itself), see the top-level [`developer/`](../../developer/overview.md) tree, specifically:

- [`developer/setup.md`](../../developer/setup.md) — local dev environment.
- [`developer/operations.md`](../../developer/operations.md) — running locally, health checks.
- [`developer/local-ai-ollama.md`](../../developer/local-ai-ollama.md) — local LLM setup for development.
- [`developer/creating-packages.md`](../../developer/creating-packages.md) — domain packages (agents, units, skills, workflows).

This `guide/developer/` section gathers end-user-facing "build & run from source" content — distinct from the platform-extension content in the top-level `developer/` tree.

Extension how-to guides that already live in this section:

- [Authoring Agent Tools](agent-tools.md) — registering custom tools from a .NET agent image via the SDK (`IToolRegistry.Register` + `app.MapToolsEndpoint(registry)`).
- [Authoring a Connector](connectors.md) — implementing `IConnectorType`, declaring a `ToolNamespace`, and shipping tools that auto-grant on unit binding.

The source-based deploy story still lives in the top-level tree linked above.
