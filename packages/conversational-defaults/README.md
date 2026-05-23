# conversational-defaults

The `sv.conversational.defaults` skill bundle (ADR-0056 Wave 2 / #2657).

A naive conversational use case — "I just want a chatbot" — shouldn't require an operator to know about `sv.messaging.send`, `sv.directory.list`, or the discovery surface. This bundle:

- **Pre-loads the fundamental-core tools** in every turn's system prompt so the runtime doesn't have to call `sv.tools.list_categories` before replying:
  - `sv.messaging.send`, `sv.messaging.multicast` (category `messaging`)
  - `sv.directory.list`, `sv.directory.lookup` (category `directory`)
  - `sv.progress.report` (category `observability`)
  - `sv.tools.list_categories`, `sv.tools.list` (category `tools`)
- **Surfaces the `memory` category** via discovery — per-tool schemas only load when the runtime queries them, so the prompt budget stays bounded.
- **Contributes the platform-layer prompt fragment** that opens with the load-bearing `[PLATFORM CONTRACT — NON-NEGOTIABLE]` header. The header is the marker that tells the runtime its terminal output is captured for diagnostics only — every side effect (including replying on the thread) goes through a tool call. ADR-0056 §8 ("Platform-prompt authority") explains why this is not stylistic: the synthesis-removed model in Wave 3 depends on the runtime actually following the contract.

The bundle is referenced as `spring-voyage/conversational-defaults:conversational-defaults` in `ai.skills:` blocks and via `spring agent skills add`.

## Coordinates

| Field | Value |
| --- | --- |
| Package | `conversational-defaults` (canonical `spring-voyage/conversational-defaults`) |
| Skill | `conversational-defaults` |
| Display name | `sv.conversational.defaults` |
| Category | platform |

## See also

- [`docs/decisions/0056-tool-only-side-effects.md`](../../docs/decisions/0056-tool-only-side-effects.md) — the design record.
- [`docs/concepts/skills.md`](../../docs/concepts/skills.md) — skill-bundle composition / Layer 2 + Layer 4 rendering.
