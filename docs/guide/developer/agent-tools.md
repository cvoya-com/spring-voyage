# Authoring Agent Tools

This guide is for developers building a Spring Voyage agent in .NET — either a fresh agent image or an extension to an existing one — who need the agent to expose actions beyond the platform's `sv.*` set and the tools any bound connector contributes. Read [Tools](../../concepts/tools.md) first for the model; this page is the authoring how-to.

## When to reach for image-tier tools

Most agents do not need image-tier tools. The platform ships `sv.*` (expertise search, message delivery, the small set of platform primitives), and a connector binding contributes everything the connector knows how to do against its external system. Reach for image-tier tools when:

- The agent integrates with a system that no shipped connector covers, **and** you do not want to author a full connector for it — for example a thin shim over a project-internal HTTP API.
- The agent needs a deterministic in-process helper (cryptography, calculation, local file inspection) that the LLM should be able to call by name.
- You are prototyping a tool surface and want it to ride along with one agent image before promoting it to a connector.

Image-tier tools are 1:1 with the subject that runs the image: there is no inheritance to children, no cross-agent reuse, and no portal toggle. If multiple units or agents need the same surface, the right home is usually a connector.

## Registering tools with the SDK

`Cvoya.Spring.AgentSdk.IToolRegistry` is the in-process seam an agent populates at startup. Each `Register` call attaches a `ToolDefinition` (the wire shape the platform persists on `image_tools`) and a handler that runs when the agent receives a tool call.

```csharp
using System.Text.Json;
using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;

public static void RegisterAcmeTools(IToolRegistry registry)
{
    registry.Register(
        new ToolDefinition(
            Name: "acme.echo",
            Description: "Echoes the input string back.",
            InputSchema: ParseSchema("""
                {
                  "type": "object",
                  "required": ["value"],
                  "properties": {
                    "value": { "type": "string" }
                  }
                }
                """)),
        handler: static (args, _) => Task.FromResult(args));

    registry.Register(
        new ToolDefinition(
            Name: "acme.timestamp",
            Description: "Returns the current UTC timestamp.",
            InputSchema: ParseSchema("""
                { "type": "object", "properties": {} }
                """)),
        handler: static (_, _) =>
        {
            var element = JsonSerializer.SerializeToElement(new
            {
                utc = DateTimeOffset.UtcNow.ToString("O"),
            });
            return Task.FromResult(element);
        });
}

static JsonElement ParseSchema(string raw)
{
    using var doc = JsonDocument.Parse(raw);
    return doc.RootElement.Clone();
}
```

Three rules the registry enforces at registration time:

- **Canonical naming.** Each id must match `ToolNaming.Pattern` (`^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`) — lowercase dotted snake_case with a leading namespace segment. A non-canonical id throws `ArgumentException` from the `ToolDefinition` constructor; the registry never sees it.
- **No duplicates.** Two registrations of the same id throw `InvalidOperationException`. Choose the namespace before sharing the image — `acme.*` for an Acme-shop image, `<your-org>.*` for a one-off internal tool, never `sv.*` (reserved for the platform) and never the slug of a shipped connector.
- **Deterministic order.** Tools come back in insertion order, so the JSON the platform caches on `image_tools` is stable and diff-able across deploys.

The handler is in-process: the platform never serialises it, never persists it, and only calls it when an inbound tool-call references the tool's id. Arguments are pre-validated by the runtime against the declared `InputSchema`; the handler does not need to re-validate.

## Exposing the registry at `/a2a/tools`

The platform-side introspector calls `GET /a2a/tools` on the agent's HTTP listener at deploy and on image rotation. For an agent built on an ASP.NET Core minimal-API host, the SDK ships an extension that wires the route onto the existing listener — no extra port, no second process:

```csharp
using Cvoya.Spring.AgentSdk;

var registry = new ToolRegistry();
RegisterAcmeTools(registry);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IToolRegistry>(registry);

var app = builder.Build();
app.MapToolsEndpoint(registry);   // GET /a2a/tools

await app.RunAsync();
```

The default path is `/a2a/tools` — the same prefix the A2A bridge uses, so the introspector hits the same endpoint regardless of whether the image is SDK-hosted or sidecar-wrapped. The extension returns a JSON array of `ToolDefinition` records (camelCase property names), which is the exact shape the platform persists on `image_tools`.

### CLI-wrapped agents (sidecar path)

Agents that wrap an opaque CLI (Claude Code, Codex, Gemini, or a custom binary) run behind the A2A sidecar and have no in-process registry to point at. The sidecar reads the tool manifest from a file path named by the `SPRING_TOOLS_MANIFEST` environment variable:

- Bake the JSON manifest into the image at a known path (e.g. `/opt/agent/tools.json`).
- Set `SPRING_TOOLS_MANIFEST=/opt/agent/tools.json` in the image so the sidecar finds it.
- The manifest is the same array shape the SDK serialises — one entry per tool with `name`, `description`, and `inputSchema`.

If the env var is unset or the file is missing, the sidecar serves an empty array; deploys of agents-without-tools succeed without per-image configuration. The A2A sidecar's [README](../../../agents/a2a-sidecar/README.md) covers the surrounding contract (`AGENT_PORT`, `AGENT_CMD`, etc.) the manifest layers onto.

## What the platform does at deploy

When an operator deploys the agent (`spring agent deploy <id>` or the equivalent portal action):

1. The dispatcher starts the container and waits for `/.well-known/agent.json` to come up on the agent port (default `8999`).
2. The platform-side introspector calls `GET /a2a/tools` against the same listener.
3. The returned array is cached onto the `image_tools` jsonb column of the subject's definition (`agent_definitions` for agents; `unit_definitions` for unit-runtime images).
4. The grant resolver merges the cached array into the subject's effective tool set with `provenance = "image:<digest>"`.

Image rotation re-runs the introspection step. A deploy that swaps the image but keeps the agent record refreshes `image_tools` from the new container's `/a2a/tools` response, so the operator sees the new surface without restarting anything else.

## Operator visibility

Every tool registered through this path surfaces in the portal's **Config → Tools** sub-tab on the agent (or on the unit, when the image runs a unit's runtime) under the **Image** section. The section is read-only: image-tier tools are tied to the running image, not to an operator toggle, so the only way to change the surface is to ship a new image.

The same data is available on the `effectiveTools` array of `AgentResponse` and `UnitResponse` — useful for programmatic consumers that need to inspect what a fleet of agents declares without iterating over the portal.

## Tradeoffs and gotchas

- **1:1 with the subject.** Image-tier tools do not inherit to children and cannot be reused across agents that run different images. If you find yourself shipping the same tool definition in two images, that is the signal to promote the surface to a connector.
- **Read-only in the portal.** An operator cannot disable a single image-tier tool without rebuilding the image.
- **Namespace discipline.** `sv.*` is reserved for the platform. Connector slugs are reserved for connectors. Pick an organisation-prefixed namespace for image tools so a future connector under the same slug does not collide with the image surface.
- **Handler purity.** Handlers run in the agent process; they share its lifetime, its filesystem, and its credentials. Use the runtime's normal patterns (cancellation tokens, scoped DI) — the SDK does not add a side framework on top.

## Worked example

A minimal end-to-end image lives at [`samples/tools-agent-image`](../../../samples/tools-agent-image/) — two `acme.*` tools, an SDK registry, and the minimal-API wiring above. The same sample is the deploy target for the platform's introspection integration test, so the manifest the sample produces is also the manifest the platform expects to see in the field.

## See also

- [Tools](../../concepts/tools.md) — the three-tier effective tool model the platform resolves at dispatch.
- [Agent runtime — The AgentSDK](../../architecture/agent-runtime.md#the-agentsdk) — the broader SDK contract (messaging client, environment variables, error model).
- [Bring Your Own Image (BYOI)](../operator/byoi-agent-images.md) — image conformance, base images, and the A2A bridge.
- [`samples/tools-agent-image/README.md`](../../../samples/tools-agent-image/README.md) — runnable reference for SDK tool registration.
