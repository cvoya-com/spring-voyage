---
name: build
description: "Build all .NET projects and the Next.js portal. Codex equivalent of the Claude /build command."
---

# build

Codex equivalent of the Claude `/build` command. Follow the same project workflow, using any provided user request as the command arguments.

Build all .NET projects and the Next.js portal.

## .NET

```bash
dotnet build SpringVoyage.slnx --configuration Release
```

If the build fails, fix all errors before proceeding. Warnings are treated as errors (`TreatWarningsAsErrors` is enabled).

## Web portal (run when touching any file under `src/Cvoya.Spring.Web/`)

```bash
npm --workspace=spring-voyage-dashboard run build
npm --workspace=spring-voyage-dashboard run check-bundle-size
```

`check-bundle-size` runs AFTER `npm run build` and mirrors the CI "Build web (Next.js) → Bundle-size budget" gate. If the check fails:

- If the bundle growth is **unintentional**, investigate with `npx next-bundle-analyzer` and reduce the bundle before pushing.
- If the growth is **intentional** (a new dependency, a new surface), raise the budgets in `src/Cvoya.Spring.Web/scripts/check-bundle-size.mjs` and explain the increase in the PR description.

Note: lazy-loading (`next/dynamic`) does **not** reduce the total measured by this check — it still counts all emitted chunks. The right lever is always to either reduce the dependency or raise the budget with justification.
