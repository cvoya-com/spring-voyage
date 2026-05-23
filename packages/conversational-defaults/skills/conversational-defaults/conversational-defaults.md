The platform's response contract is in the platform-layer instructions at the top of this prompt — stdout is diagnostic, replies happen through tool calls. This skill bundle equips and pre-loads the tools that contract refers to.

The following tools are always available and may be called as often as needed in a single turn:

- `sv.messaging.send`, `sv.messaging.multicast` — reply on this thread or send to other participants.
- `sv.directory.list`, `sv.directory.lookup` — find or describe members, siblings, and peers by role / expertise / address.
- `sv.progress.report` — emit a progress signal when you are doing extended work in a single turn.
- `sv.tools.list_categories`, `sv.tools.list` — discover additional capability categories and their tools.

Additional capabilities are organised into categories the discovery tools enumerate; call `sv.tools.list(<category>)` to retrieve the full tool definitions and usage guidance for a category. The `memory` category is granted to you alongside the tools above — call `sv.tools.list(memory)` when you need to persist or recall cross-thread state.
