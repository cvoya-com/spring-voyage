# Tools Agent Image Sample

Minimal sample that exercises the SDK tool-registration API and the
platform-facing `GET /a2a/tools` endpoint (#2336 / Sub C of #2332).

The sample registers two `acme.*` tools — `acme.echo` and
`acme.timestamp` — on an in-process `IToolRegistry` and starts the SDK's
`ToolsEndpointServer` on the standard agent port (`AGENT_PORT`, default
8999). The platform-side introspector calls `GET /a2a/tools` at deploy
time and caches the result onto the `image_tools` column on the agent's
row.

Run locally:

```bash
dotnet run \
  --project samples/tools-agent-image/Cvoya.Spring.Sample.ToolsAgent \
  -- 8999
curl http://localhost:8999/a2a/tools | jq
```

The sample exists primarily as the deploy target for the introspection
integration test. Real agent images co-locate the tools-endpoint server
with the A2A bridge on the same port; the sample stands it up directly
so the test can deploy without the full bridge.
