# `agents/` — operator-shipped agent images

This directory holds the ready-to-run agent images that Spring Voyage **ships to operators**. Each subdirectory is a self-contained container build (Dockerfile, runtime code, per-agent Dapr config when needed):

- `a2a-sidecar/` — Python A2A protocol sidecar bundled into every agent image.
- `spring-voyage-agent/` — the canonical Spring Voyage Python agent runtime (Dapr-aware), used by the OSS image.
- `spring-voyage-agent-sdk/` — the **Python** SDK consumed by `spring-voyage-agent` and operator-authored Python agents.

## Not to be confused with: `src/Cvoya.Spring.AgentSdk`

`src/Cvoya.Spring.AgentSdk/` is the **.NET** SDK that developers reference from their own C# projects when authoring custom agents. It is published as a NuGet package; it has no overlap with the container images here.

| You want to… | Go here |
|---|---|
| Build / modify an agent container that ships to operators | `agents/<image>/` |
| Author a custom agent in **Python** | `agents/spring-voyage-agent-sdk/` |
| Author a custom agent in **C#** / .NET | `src/Cvoya.Spring.AgentSdk/` |
