# Release notes

Human-written release notes — one file per release line — that become the body
of the GitHub Release. This is the **narrative** companion to
[`CHANGELOG.md`](../../CHANGELOG.md): the changelog is the exhaustive,
per-PR technical record; these files are the readable "what's in this release
and why you'd care" story for people landing on the Releases page.

## How it works

When a `spring-voyage-v<version>` tag is pushed, the
[`release.yml`](../../.github/workflows/release.yml) `create-draft-release` job
calls [`eng/release/resolve-release-notes.sh`](../../eng/release/resolve-release-notes.sh)
to choose the release body, in this order:

1. `docs/releases/<full-version>.md` — exact, e.g. `1.0.0-rc.1.md` (rarely needed).
2. `docs/releases/<major.minor.patch>.md` — the **release line**, e.g. `1.0.0.md`.
3. `CHANGELOG.md` `[Unreleased]` — fallback when no curated file exists.

The workflow then appends, in order, a git-cliff-generated **What changed in
this release** delta (the grouped Conventional-Commit changes since the previous
tag) and the canonical **Release assets** tables (container images + attached
files) — both automatically. So keep these files to the high-level narrative:
**do not** put image/asset tables here, and **do not** hand-maintain a per-PR
change list either; the delta covers it and you'd duplicate it.

The common case is **(2)**: keep one file per release line (`1.0.0.md`) and let
every pre-release (`-alpha`/`-beta`/`-rc`) and the final stable cut of that
version reuse it. Update it as features land. Because the file is read from the
**tagged commit**, it must be committed (merged to `main`) *before* the release
tag is cut.

## Authoring with a coding assistant

Run the [`/release-notes`](../../.claude/commands/release-notes.md) command in
Claude Code to draft or refresh the file for a version. It reads `CHANGELOG.md`
`[Unreleased]`, the root `README.md`, and the previous release-line notes, then
writes `docs/releases/<version>.md`. Review it, tighten the narrative, and commit
it in the release-prep PR.

You can also point it at extra source material (an announcement draft, a blog
post) — pass the path as an argument.

## Conventions

- **Audience:** someone discovering the project on the Releases page, not a
  maintainer. Lead with what it is and how to try it.
- **Keep claims accurate.** Verify against the code/README before stating a
  feature ships; the changelog and the shipped packages are the source of truth.
- **No asset/image tables** (the workflow appends them).
- **No per-PR change list** — the auto-generated *What changed in this release*
  delta covers it; keep these notes to the narrative.
- **Link to canonical pages:** [spring.voyage](https://spring.voyage) and
  [CVOYA](https://cvoya.com), plus docs/issues/discussions.
- Keep it scannable: short sections, bullets over paragraphs.

See the full release process in
[`docs/developer/releases.md`](../developer/releases.md#release-notes).
