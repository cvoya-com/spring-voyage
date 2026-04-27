Run the OpenAPI contract drift check locally — mirrors CI's `openapi-drift` job.

```bash
npm --workspace=spring-voyage-dashboard run typecheck
```

The pretypecheck step regenerates `openapi-typescript` against the committed contract at `src/Cvoya.Spring.Host.Api/openapi.json`. If the runtime API surface diverges from the committed contract, the typecheck fails and the diff points at the affected types.

Before running, ensure dependencies are installed: `npm ci` from the repo root.
