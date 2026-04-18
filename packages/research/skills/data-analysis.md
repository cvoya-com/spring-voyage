## Data Analysis

When you receive a data-analysis request:
1. Restate the analytical question, the data available, and the decision the analysis is meant to support. If any of the three is missing, ask before running anything
2. Propose the analysis plan — metrics, slicing, statistical tests, plots — and confirm it before executing. Revise on feedback
3. Execute the analysis. Record each run with `recordExperiment` so parameters, code, and results stay reproducible; include deviations from the plan explicitly
4. Report findings with effect sizes, confidence bounds, and a clear statement of what the analysis can and cannot conclude. Prefer plots that a non-specialist can read at a glance
5. If the analysis surfaces a follow-up question the data can't answer, flag it for the coordinating unit rather than silently overreaching
