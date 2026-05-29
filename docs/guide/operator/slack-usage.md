# Using the Slack bot

Once [`spring connector slack install`](slack-app-setup.md) has succeeded and you've clicked **Connect Slack** in the portal, the bot opens a DM with you and posts an install greeting. This guide covers what you can do from there: the slash commands, how Spring Voyage threads map onto Slack threads, the refusal behaviours you may hit, and a one-minute smoke test to confirm the integration end-to-end.

The design this guide operationalises is [ADR-0061](../../decisions/0061-slack-connector-oss-shape.md); section references below point at it.

## Where the bot lives

OSS v0.1 is **single-bound-user, DM-only** (ADR-0061 §§ 2.1, 2.2):

- **One bound user.** The bound user is whoever ran the OAuth install (clicked **Connect Slack**). That Slack account maps to the deployment's Spring Voyage operator user. Other people in the workspace exist in Slack but are unbound on the SV side — see [Refusal behaviours](#refusal-behaviours).
- **The bot only operates in its DM with you.** Everything — slash commands, agent replies, your messages back — happens in the direct message between you and the bot. There is no channel surface in v0.1.
- **It auto-leaves channels.** If the bot is invited to any channel, it posts a single message and immediately leaves:

  > This Spring Voyage install is bound to one user and only operates in DM with that user. Leaving.

  This is expected behaviour, not a failure. The bot does not subscribe to channel-message or `@mention` events, so inviting it anywhere has no effect beyond that one leave message.

## The three slash commands

Type any of these in the **DM with the bot** (running `/sv-help` shows the same cheat sheet in-Slack):

> *Spring Voyage Slack cheat sheet*
> • `/sv-thread` — start a new SV thread with one or more agents, units, or humans.
> • `/sv-threads` — list your active SV threads with deep links to each.
> • `/sv-help` — show this message.
>
> All commands operate in this DM with the SV bot only.

### `/sv-thread` — start a thread

Opens a **Start an SV thread** modal with two fields:

- **Participants** — a multi-select listing the agents, units, and other SV humans in your directory ("Pick agents, units, or humans"). Your own primary identity is excluded — you're always implicitly on the thread. Pick one or more.
- **Initial message (optional)** — a free-text box. If filled in, it's sent as your first message on the new thread; if left blank, the thread is created empty and waits for your first reply.

Press **Create**. The bot then, in your DM:

1. Resolves or creates the Spring Voyage thread for `{you, …the participants you picked}`.
2. Posts a **parent message** whose text is a human-readable slug naming the participants — e.g. picking the agent *bob* and the unit *research* yields `sv-bob-research` (ADR-0061 §4).
3. Records the Slack thread ↔ SV thread mapping, so replies route correctly.
4. If you supplied an initial message, delivers it to the participants — their replies arrive as threaded replies under the parent.

### `/sv-threads` — list your threads

Opens a **SV threads** modal listing every SV thread you're a participant in that has a Slack surface in this workspace. Each row deep-links to that thread's parent message for one-click navigation. The link is a canonical Slack permalink where one can be resolved, falling back to a workspace-local `slack://` deep link otherwise.

If you have none yet, the modal says:

> No active SV threads in this workspace yet. Use `/sv-thread` to start one.

### `/sv-help` — show the cheat sheet

Posts the cheat sheet above into the DM. No side effects.

## How SV threads map onto Slack threads

The connector models **one Slack thread per SV thread**, all inside your bot DM (ADR-0061 §3):

- **The parent message is the slug.** The first time the bot surfaces an SV thread, it posts a parent message in the DM (the `sv-…` slug). That message's `thread_ts` is recorded as the SV thread's Slack anchor.
- **Replies stay in the thread.** Every later message on that SV thread — yours or a participant's — is posted as a threaded reply under that parent. To respond, **reply inside the Slack thread**, not at the top level of the DM. A top-level DM message (no thread) has no SV thread to route to and is dropped (recorded in the audit log as `dropped:no-thread`). Likewise, replying in a Slack thread the bot didn't create is dropped (`dropped:unknown-thread_ts`).
- **Participants post under their own persona.** When the bot relays a message from an agent, unit, or non-bound human, it sets the post's name and avatar to that participant's SV display name and icon (via the `chat:write.customize` scope). Your own messages keep your native Slack identity.
- **All-non-human threads have no Slack surface.** A thread between, say, two agents and no human has nothing to show in Slack in v0.1; observe those from the portal.

## Refusal behaviours

| What you see | Why | Where it's defined |
|---|---|---|
| **"This Spring Voyage install is bound to one Slack user. You don't have access."** — sent once, then silence | A workspace member who is **not** the bound user DM'd the bot. Only the OAuth installer is bound in v0.1; the bot replies once and ignores further messages from that user. | ADR-0061 §2.4 |
| The **same** refusal text, shown only to you (ephemeral) | You ran a `/sv-*` command **outside the bot DM** (in a channel or another DM). The commands are DM-only. | ADR-0061 §5 |
| Slack shows **`dispatch_failed`** after a slash command | A Slack-generated error, not an SV message. It means Slack could not deliver the command to your deployment: the platform isn't reachable at the registered slash-command URL, the signing-secret signature check failed, or the command was invoked outside the DM. | See [slack-app-setup.md → Common failures](slack-app-setup.md#verifying-the-install) |
| **"Sending messages to this app has been turned off"** in the DM compose box | The Slack app's **Messages Tab** is disabled. Fresh installs from the current manifest enable it; an app created before that fix needs the tab toggled on manually. | [slack-app-setup.md](slack-app-setup.md) (App Home → Show Tabs) |

Every refusal is observable in the connector's inbound audit log — it's recorded, never silent.

## One-minute smoke test

Run this right after install to confirm the round trip works end-to-end:

1. Open the **DM with the bot** in Slack (under **Direct messages**, the app's bot user). Confirm you can type in the compose box — if it's greyed out with "Sending messages to this app has been turned off", fix the Messages Tab first ([slack-app-setup.md](slack-app-setup.md)).
2. Send **`/sv-help`**. Expect the cheat sheet to appear.
3. Send **`/sv-thread`**, pick **one agent** in the participant picker, type a short initial message (e.g. "hello"), and press **Create**.
4. Expect a **parent message** (the `sv-…` slug) in the DM, with the agent's reply arriving as a **threaded reply** under it, posted under the agent's name and avatar.
5. **Reply inside that thread** with a follow-up. Expect the agent to respond again in the same thread — confirming inbound routing works in both directions.

If steps 4–5 work, the connector is wired end-to-end: outbound delivery, persona rendering, and inbound routing are all live.

## Related documentation

- [Register your Slack app](slack-app-setup.md) — installing the app, credentials, OAuth, Socket Mode forwarder, and install troubleshooting.
- [Connectors operator guide](connectors.md) — installing, inspecting, and uninstalling connectors per tenant.
- [ADR-0061 — Slack connector v0.1 OSS shape](../../decisions/0061-slack-connector-oss-shape.md) — the OSS restrictions, thread-mapping model, and slash-command surface this guide operationalises.
