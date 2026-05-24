# conversational-defaults

The `sv.conversational.defaults` skill bundle (ADR-0056 Wave 2 / #2657; reshaped by #2670).

A naive conversational use case — "I just want a chatbot" — shouldn't require an operator to know about `sv.messaging.send`, `sv.directory.list`, or the discovery surface. After #2670 that knowledge lives in Layer 1 (`PlatformPromptProvider`); this bundle adds the conversation-shaped extras on top:

- **Points at the platform-layer contract.** Layer 1 already emits the `[PLATFORM CONTRACT — NON-NEGOTIABLE]` header and names every always-available platform tool (`sv.messaging.*`, `sv.directory.*`, `sv.progress.report`, `sv.tools.*`). The bundle prompt tells the runtime where to read that contract rather than duplicating it — per #2670, packages must never re-state the platform-generated tooling instructions.
- **Surfaces the `memory` category** as the one package-specific grant on top of the always-available core. Per-tool schemas only load when the runtime calls `sv.tools.list(memory)`, so the prompt budget stays bounded.

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
