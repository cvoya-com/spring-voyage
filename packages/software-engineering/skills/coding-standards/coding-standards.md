## Coding Standards

When writing or reviewing code, ensure the following standards are met:

1. **Follow project conventions** — adhere to the coding style, naming patterns, and architecture decisions established in the project
2. **Write tests for all changes** — every new feature or bug fix must include corresponding unit tests at minimum
3. **Keep PRs focused** — each pull request should address a single concern; avoid mixing unrelated changes
4. **Use meaningful commit messages** — describe what changed and why, not just what files were touched
5. **Review before submitting** — self-review your diff before requesting external review
6. **Handle errors explicitly** — do not swallow exceptions silently; log and propagate appropriately
7. **Document public APIs** — add XML doc comments or equivalent for all public types and methods
