Scaffold a new ADR in `docs/decisions/`.

## Steps

1. Find the next number: `ls docs/decisions/0*.md | tail -1` and increment.
2. Create the file as `docs/decisions/0NNN-<short-kebab-slug>.md` with this skeleton:

   ```markdown
   # 00NN — <Title>

   - **Status:** Proposed — <one-line summary of what this decision locks in>
   - **Date:** YYYY-MM-DD
   - **Related:** <related ADRs / issues / PRs>
   - **Related code:** <relevant filepaths>

   ## Context

   What problem are we solving? What is the current state? What forces are pushing the decision?

   ## Decision

   What we are deciding, stated as a clear directive.

   ## Consequences

   What this implies — including what becomes easier, what becomes harder, and what is **not** abstracted by this decision.
   ```

3. Add a row to `docs/decisions/README.md` index with the ADR number, title, and current Status.
4. Land in the same PR as the code or decision it captures, where applicable.

Look at recent ADRs (e.g. `docs/decisions/0029-*.md`) for style and depth — short summaries on top, full reasoning in the body.
