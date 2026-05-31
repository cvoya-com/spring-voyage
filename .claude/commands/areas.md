Enumerate the active Spring Voyage `area:*` taxonomy and the owning umbrella for each.

## Steps

1. Discover the active plan version. Plans live under `docs/plan/`,
   one directory per version (`v0.1/`, `v0.2/`, …). The active
   version is the most recent one — when in doubt, list the
   directory and pick the highest version number, then read its
   `README.md` to confirm scope:

   ```bash
   ls docs/plan/
   # for the active version (the highest):
   ACTIVE="$(ls -d docs/plan/v* | sort -V | tail -n1)"
   ```

2. List the area files for that version:

   ```bash
   ls "${ACTIVE}/areas/"
   ```

   Each file is named `<area-code>-<short-slug>.md`. The `area-code`
   is the leading letter(s) before the first hyphen (e.g. `a`, `e1`,
   `j`). The `area:*` label is `area:<area-code>-<short-slug>`
   (matches the filename minus the extension), or — when the
   per-area README has a different canonical slug — whatever the
   area's own front-matter declares.

3. For each area, read the first heading and the short summary at
   the top of the file:

   ```bash
   for f in "${ACTIVE}"/areas/*.md; do
     printf '== %s\n' "$f"
     head -10 "$f"
     printf '\n'
   done
   ```

4. Render the output as a table the operator can scan:

   | Area code | Filename | Title | Owning umbrella issue (if linked) |
   | --------- | -------- | ----- | --------------------------------- |

   Pull the owning umbrella issue from any `**Umbrella issue:** #N` /
   `**Tracking:** #N` line in the area's header block. If absent,
   leave the column blank — it is not always set.

5. If the operator asked to triage a specific issue, also call out
   which areas overlap based on the touched file paths or symbols in
   the issue body. Use `gh issue view <N>` to fetch the body.

The plan-of-record is the active version's `README.md`; the per-area
files under that version's `areas/` subdirectory are the
authoritative source for the `area:*` label taxonomy. Treat any area
not listed under that directory as obsolete or not yet adopted.
