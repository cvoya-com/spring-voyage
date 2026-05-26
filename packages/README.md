# Catalog Packages

Spring Voyage ships a set of catalog packages that are automatically visible in the tenant catalog on first boot — no registration step required. They fall into two groups: **example packages** that illustrate specific platform features, and **domain packages** that ship production-ready agent teams for real workflows.

## Example packages

These packages exist to demonstrate platform capabilities in isolation. They are deliberately simple and do not require connector configuration unless noted.

| Package | What it illustrates |
| --- | --- |
| [`hello-world`](hello-world/) | Minimal baseline: one unit, one agent, no connector, no skills. Good starting point for understanding the install pipeline and activation lifecycle. |
| [`example-simple`](example-simple/) | Recursive folder layout (ADR-0043): a multi-agent unit where each agent owns its own skills in a nested folder. Every artefact is authored as a concrete folder — no templates, no `from:` clones. |
| [`example-templated`](example-templated/) | Type / instance separation: an `AgentTemplate` instantiated multiple times via `from:`, and a `UnitTemplate` with stamped children. Shows how to share a single definition across many running instances. |

## Domain packages

These packages ship working agent teams for real-world workflows. They are connector-aware and can be installed as-is or used as starting points for customisation.

| Package | What it illustrates |
| --- | --- |
| [`research`](research/) | Package-level skills shared across multiple agents; optional connectors (arxiv, web-search) that the unit can use but does not require at install time. |
| [`magazine`](magazine/) | A goal-driven editorial team that produces a daily online edition. A central editor (the unit) sets direction, six specialist agents handle pitching, reporting, fact-check, copy, packaging, and assembly, and a human publisher signs off before publication. Uses the web-search connector for sourcing. |
| [`product-management`](product-management/) | Required GitHub connector with install-time inputs; package-level skills consumed by every unit member; PM + designer role split. |
| [`software-engineering`](software-engineering/) | Required GitHub connector; a checked-in Dapr workflow (`software-dev-cycle`) shipped alongside the YAML manifests; tech lead / engineer / QA role split. |
| [`spring-voyage-oss`](spring-voyage-oss/) | Umbrella unit over multiple sub-units; package-level execution inheritance; five engineer instances + two PM instances stamped from shared templates; CI-aligned slash-command skills. This is the dogfooding package — it uses Spring Voyage to develop Spring Voyage itself. |
