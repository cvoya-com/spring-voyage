---
name: release-notes
description: "Draft or refresh the human-written release notes for a version in `docs/releases/`. Codex equivalent of the Claude /release-notes command."
---

# release-notes

Codex equivalent of the Claude `/release-notes` command. Follow the same project workflow, using any provided user request as the command arguments.

Draft or refresh the human-written release notes for a version in `docs/releases/`.

These become the GitHub Release body (see [`docs/releases/README.md`](../../docs/releases/README.md)). They are the narrative companion to `CHANGELOG.md`, not a copy of it.

## Arguments

- `$ARGUMENTS` — the target version or release line (e.g. `1.0.0`). Optional second token: a path to extra source material (an announcement draft, a blog post) to borrow framing from.
- If no version is given, infer the current release line from the latest `git tag -l 'spring-voyage-v*'` and `docs/plan/`.

## Steps

1. **Gather source material:**
   - `eng/release/extract-changelog-section.sh Unreleased` — the technical record of what's shipping.
   - The root `README.md` — positioning, building blocks, install flow, license, container images.
   - The previous release-line file in `docs/releases/` (if any) — to keep voice and structure consistent.
   - Any extra path passed in `$ARGUMENTS`.
2. **Verify before claiming.** Don't trust draft prose blindly. Confirm a feature actually ships before stating it: check the code, `packages/` (which teams ship), and the connectors under `src/Cvoya.Spring.Connector.*`. Stale "not implemented" caveats are worse than omission.
3. **Write `docs/releases/<line>.md`** (e.g. `docs/releases/1.0.0.md`), audience = someone discovering the project on the Releases page:
   - Lead: what Spring Voyage is, that it's a **[CVOYA](https://cvoya.com)** project, links to [spring.voyage](https://spring.voyage) and CVOYA.
   - An honest status/alpha callout when pre-1.0.
   - Capability-oriented highlights (not internal phase numbers).
   - Install one-liner, getting-started links, ready-made packages, open-core + BSL license, feedback channels (issues, discussions), links.
   - **No container-image or release-asset tables** — `release.yml` appends those automatically.
   - **No exhaustive per-PR change list** — `release.yml` appends a git-cliff *What changed in this release* delta automatically. Keep highlights capability-oriented; don't restate every PR.
4. **Keep it scannable** — short sections, bullets over paragraphs. Match the conventions in [`docs/releases/README.md`](../../docs/releases/README.md).
5. Stage the file for the release-prep PR. It must be merged to `main` **before** the release tag is cut (the workflow reads it from the tagged commit).

Do not edit `CHANGELOG.md` here — that follows its own per-PR convention in `CONTRIBUTING.md`.
