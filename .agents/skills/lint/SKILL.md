---
name: lint
description: "Run every lint-class CI gate locally. Codex equivalent of the Claude /lint command."
---

# lint

Codex equivalent of the Claude `/lint` command. Follow the same project workflow, using any provided user request as the command arguments.

Run every lint-class CI gate locally.

This skill mirrors the CI jobs that block merge for formatting / static-quality
reasons. CI failures on these gates are common because the .NET formatter
(`dotnet format`) and the web-side linters live in different toolchains and
"my change is small" doesn't excuse skipping any of them — `dotnet format`
catches trailing-newline / whitespace errors that no .NET unit test will surface.

The pre-push hook (`eng/ci/ci-local.sh`) runs these for you; you can also run
them by hand from the repository root. Each command must exit 0; fix and re-run
any that don't.

```bash
# .NET — formatting + whitespace + final-newline (CI: "Format check")
dotnet format SpringVoyage.slnx --verify-no-changes

# Web — ESLint, Knip dead-code, tsc typecheck (CI: "Lint web", "Dead-code check (knip)", "Typecheck web (tsc)")
npm run lint
npm --workspace=spring-voyage-dashboard run knip
npm --workspace=spring-voyage-dashboard run typecheck

# Python agents — ruff lint + format check (CI: "Lint Python agents")
# Run unconditionally; cheap (~10s) and CI runs it whenever any agents/ file changes.
# If ruff is not installed: pip install ruff   (CI pins no version; takes the latest)
ruff check agents/spring-voyage-agent/
ruff check agents/spring-voyage-agent-sdk/
ruff format --check agents/spring-voyage-agent/
ruff format --check agents/spring-voyage-agent-sdk/
```

If `dotnet format` reports issues, fix them with:

```bash
dotnet format SpringVoyage.slnx
```

If `ruff format --check` reports issues, fix them with:

```bash
ruff format agents/spring-voyage-agent/ agents/spring-voyage-agent-sdk/
```

Re-run the `--verify-no-changes` / `--check` variants to confirm. ESLint auto-fix is `npm run lint:fix`.

**Ruff version skew:** CI installs ruff via `pip install ruff` (no pinned version) so the canonical format can drift across CI runs. If `ruff format --check` passes locally but CI disagrees, install the version CI is using into a temp venv (e.g. `python3 -m venv /tmp/ruff && /tmp/ruff/bin/pip install ruff==<ci-version>`) and re-run.

Other CI lint jobs that are NOT in this skill because they are
narrowly scoped or run only when their files actually change — invoke
explicitly when touching the relevant area:

- `Lint agent definitions` — script in `.github/workflows/ci.yml` job
  `lint-agent-definitions` (when editing `agents/*/agent.yaml`)
- `Lint connector web submodules` — `eslint src/Cvoya.Spring.Connector.*$web`
  (already covered by `npm run lint` above on most setups)
- `Lint docs (evergreen framing)` — script under `scripts/lint-docs.sh`
  (when editing `docs/`)
- `OpenAPI contract drift` — see the `$openapi-diff` skill (when editing
  API endpoints or DTOs)
- `EF Core model drift` — see `/ef-drift` if it exists, or rebuild the
  solution and check `git status` on `Migrations/` (when editing entities)

The `$build` and `$test` skills are separate; CI and the merge queue enforce them.
