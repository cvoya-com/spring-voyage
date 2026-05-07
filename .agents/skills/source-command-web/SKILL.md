---
name: "source-command-web"
description: "Run the migrated source command `web`."
---

# source-command-web

Use this skill when the user asks to run the migrated source command `web`.

## Command Template

Start the Spring.Web (portal) dev server.

```bash
cd src/Cvoya.Spring.Web && npm run dev
```

If dependencies are missing, run `npm ci` from the repo root first — the workspace is `spring-voyage-dashboard`.

## MANUAL MIGRATION REQUIRED

Migrated from source command `web` into a Codex skill. Invoke it as `$source-command-web` and manually rewrite any slash-command behavior that depended on provider-specific runtime expansion.
