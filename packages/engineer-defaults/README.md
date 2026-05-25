# engineer-defaults

The `sv.engineer.defaults` skill bundle (#2745).

The platform-layer concurrent-threads guard names the two things the platform isolates per thread (workspace subtree + session storage) and the constraints that follow from what is shared (ephemeral ports, no process-global mutation). It stays universal so PM, analyst, and router agents see no signal noise. This bundle layers software-engineering-shaped guidance on top — the shell-tooling footguns that only matter for agents whose runtime is CLI-shell-heavy.

Equip this bundle on engineer-shaped agents alongside the platform core; non-engineer agents leave it off.

## Coordinates

| Field | Value |
| --- | --- |
| Package | `engineer-defaults` (canonical `spring-voyage/engineer-defaults`) |
| Skill | `engineer-defaults` |
| Display name | `sv.engineer.defaults` |
| Category | platform |

## See also

- [`docs/decisions/0041-actor-runtime-contract.md`](../../docs/decisions/0041-actor-runtime-contract.md) — the `concurrent_threads` two-mode contract this bundle layers onto.
- [`docs/decisions/0056-tool-only-side-effects.md`](../../docs/decisions/0056-tool-only-side-effects.md) — the tool-only-side-effects framing the bundle's `sv.tools.*` reminder rides on.
- [`docs/concepts/skills.md`](../../docs/concepts/skills.md) — skill-bundle composition / Layer 4 rendering.
