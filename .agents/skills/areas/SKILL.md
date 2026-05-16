---
name: "areas"
description: "Enumerate the current Spring Voyage v0.1 `area:*` taxonomy and the owning umbrella for each."
---

# Areas

Use this skill when the user asks to run `/areas`, list the v0.1 areas,
or look up which `area:*` label applies to an issue.

## Command Template

Enumerate the active v0.1 areas declared under `docs/plan/v0.1/areas/`.

## Steps

1. List the area files:

   ```bash
   ls docs/plan/v0.1/areas/
   ```

   Each file is named `<area-code>-<short-slug>.md`. The `area-code` is
   the leading letter(s) before the first hyphen (e.g. `a`, `e1`, `j`).
   The `area:*` label is `area:<area-code>-<short-slug>` (matches the
   filename minus the extension), or — when the per-area README has a
   different canonical slug — whatever the area's own front-matter
   declares.

2. For each area, read the first heading and the short summary at the
   top of the file:

   ```bash
   for f in docs/plan/v0.1/areas/*.md; do
     printf '== %s\n' "$f"
     head -10 "$f"
     printf '\n'
   done
   ```

3. Render the output as a table the operator can scan:

   | Area code | Filename | Title | Owning umbrella issue (if linked) |
   | --------- | -------- | ----- | --------------------------------- |

   Pull the owning umbrella issue from any `**Umbrella issue:** #N` /
   `**Tracking:** #N` line in the area's header block. If absent, leave
   the column blank — it is not always set.

4. If the operator asked to triage a specific issue, also call out
   which areas overlap based on the touched file paths or symbols in
   the issue body. Use `gh issue view <N>` to fetch the body.

The plan-of-record is `docs/plan/v0.1/README.md`; the per-area files
under `docs/plan/v0.1/areas/` are the authoritative source for the
`area:*` label taxonomy. Treat any area not listed under that
directory as obsolete or not yet adopted.
