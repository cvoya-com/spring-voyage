# conversational-defaults

The default skill bundle every agent inherits. It's tuned for the simplest case — "I just want a chatbot" — so a basic conversational agent needs no extra setup.

The platform already tells each agent how to reply and which tools are always available. This bundle adds one thing on top: it grants the `memory` category, so an agent can persist and recall state across conversations. The per-tool details for that category load on demand — only when the agent actually uses them — so the prompt stays small.

Reference it as `spring-voyage/conversational-defaults` in an `ai.skills:` block or with `spring agent skills add`.

## Coordinates

| Field | Value |
| --- | --- |
| Package | `conversational-defaults` (canonical `spring-voyage/conversational-defaults`) |
| Skill | `conversational-defaults` |
| Display name | `sv.conversational.defaults` |
| Category | platform |
