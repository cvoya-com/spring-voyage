# Catalog Packages

Spring Voyage ships a set of catalog packages that appear in your tenant catalog automatically on first boot — no registration step. Browse them in the portal under **New Unit → Catalog** or with `spring package list`, and install one with `spring package install <name>`.

They fall into two groups: **examples** that teach a single concept, and **ready-to-run teams** for real workflows.

## Examples

Small packages that show one idea clearly. Both install connector-free, so you can try them with a single command.

| Package | What it shows |
| --- | --- |
| [`hello-world`](hello-world/) | The minimal package — one unit, one agent, no connector, no skills. The place to start. |
| [`templated-team`](templated-team/) | Reusable templates — define an agent or unit once and stamp out many running instances with `from:`. |

## Ready-to-run teams

Working multi-agent teams for real workflows. Install them as-is, or use them as a starting point for your own.

| Package | What it does |
| --- | --- |
| [`research`](research/) | A research cell — a generalist researcher, a literature reviewer, and a data analyst — that takes a question and returns a sourced answer. Optional arxiv / web-search connectors. |
| [`magazine`](magazine/) | A goal-driven editorial team that produces a daily edition: an editor sets direction, six specialists carry each story from pitch through fact-check and copy to assembly, and a human publisher signs off. Uses web search for sourcing. |
| [`product-management`](product-management/) | A product squad — a product manager and a designer — wired to a GitHub repository, with triage, roadmap, sprint-planning, and design-review skills. |
| [`software-engineering`](software-engineering/) | A software engineering team — tech lead, backend engineer, and QA — wired to a GitHub repository. Picks up issues and PRs, develops on worktrees, and opens pull requests. |

## For Spring Voyage contributors

[`spring-voyage-oss`](spring-voyage-oss/) is the team that develops Spring Voyage itself, running on the platform. It's tailored to this project's repository and workflow — install it if you're contributing to Spring Voyage, or read it as a worked example of a larger production team.
