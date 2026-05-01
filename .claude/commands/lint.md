Run every lint-class CI gate locally.

This skill mirrors the CI jobs that block merge for formatting / static-quality
reasons. CI failures on these gates are common because the .NET formatter
(`dotnet format`) and the web-side linters live in different toolchains and
"my change is small" doesn't excuse skipping any of them — `dotnet format`
catches trailing-newline / whitespace errors that no .NET unit test will surface.

Run all of these from the repository root before pushing. Each command must
exit 0; fix and re-run any that don't.

```bash
# .NET — formatting + whitespace + final-newline (CI: "Format check")
dotnet format SpringVoyage.slnx --verify-no-changes

# Web — ESLint, Knip dead-code, tsc typecheck (CI: "Lint web", "Dead-code check (knip)", "Typecheck web (tsc)")
npm run lint
npm --workspace=spring-voyage-dashboard run knip
npm --workspace=spring-voyage-dashboard run typecheck
```

If `dotnet format` reports issues, fix them with:

```bash
dotnet format SpringVoyage.slnx
```

then re-run `--verify-no-changes` to confirm. ESLint auto-fix is `npm run lint:fix`.

Other CI lint jobs that are NOT in this skill because they are either
narrowly scoped or run only when their files actually change — invoke
explicitly when touching the relevant area:

- `Lint Python agents` — `ruff check agents/` (when editing `agents/`)
- `Lint agent definitions` — script in `.github/workflows/ci.yml` job
  `lint-agent-definitions` (when editing `agents/*/agent.yaml`)
- `Lint connector web submodules` — `eslint src/Cvoya.Spring.Connector.*/web`
  (already covered by `npm run lint` above on most setups)
- `Lint docs (evergreen framing)` — script under `scripts/lint-docs.sh`
  (when editing `docs/`)
- `OpenAPI contract drift` — see the `/openapi-diff` skill (when editing
  API endpoints or DTOs)
- `EF Core model drift` — see `/ef-drift` if it exists, or rebuild the
  solution and check `git status` on `Migrations/` (when editing entities)

The `/build` and `/test` skills are separate and remain mandatory pre-push.
