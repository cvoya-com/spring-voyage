---
name: "openapi-diff"
description: "Run the Spring Voyage OpenAPI contract drift check."
---

# OpenAPI Diff

Use this skill when the user asks to run `/openapi-diff`, verify OpenAPI contract drift, or refresh-check generated web API types.

## Command Template

Run the OpenAPI contract drift check locally — mirrors CI's `openapi-drift` job.

```bash
npm --workspace=spring-voyage-dashboard run typecheck
```

The pretypecheck step regenerates `openapi-typescript` against the committed contract at `src/Cvoya.Spring.Host.Api/openapi.json`. If the runtime API surface diverges from the committed contract, the typecheck fails and the diff points at the affected types.

Before running, ensure dependencies are installed: `npm ci` from the repo root.
