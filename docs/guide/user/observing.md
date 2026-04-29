# Observing Activity

This guide covers how to monitor agent activity, track costs, and use the dashboard.

## Activity Streams

### Stream Unit Activity (Real-Time)

```
spring activity stream --unit engineering-team
```

This streams all activity events from the unit and its members in real-time. You'll see:

- Messages sent and received
- Conversations starting and completing
- Decisions being made
- Errors and warnings
- Tool calls and results
- Cost events

Press `Ctrl+C` to stop streaming.

### Stream a Specific Agent

```
spring activity stream --agent ada --unit engineering-team
```

### Filter by Event Type

```
spring activity stream --unit engineering-team --type error,warning
spring activity stream --unit engineering-team --type decision,conversation-completed
```

### View Activity History

```
spring activity history --unit engineering-team --since "2 hours ago"
spring activity history --agent ada --since "yesterday"
```

## Conversations and Inbox

Activity is the raw, chronological log; **conversations** are the narrative view of one specific thread. Both surfaces share the same underlying event store — a conversation is just the subset of activity events that carry the same correlation id (the conversation id assigned by the messaging layer).

### List and Show Conversations

```
spring conversation list
spring conversation list --unit engineering-team
spring conversation list --agent ada
spring conversation list --status active
spring conversation list --participant human://savasp
spring conversation show <conversation-id>
```

`list` prints one row per conversation — id, status, origin, participants, event count, last activity, opening summary. Filters narrow by unit, agent, participant address, or status (`active` / `completed`). `show` prints the conversation header followed by the ordered event timeline so you can read the back-and-forth in context. Both accept `--output json`.

### Respond to an Existing Conversation

To post a new message into a thread the agent is already working on — without starting a new conversation — use either of the equivalent forms:

```
spring conversation send --conversation <id> agent://engineering-team/ada "Looks good — ship it."
spring message send agent://engineering-team/ada "Looks good — ship it." --conversation <id>
```

Both resolve to the same server endpoint; pick whichever reads better in the surrounding script.

### Close a Conversation (#1038)

When a conversation hangs, fails, or simply needs to be abandoned — for example, a delegated container exited non-zero (#1036) and the agent is now wedged on a stale active slot — close it explicitly:

```
spring conversation close <conversation-id>
spring conversation close <conversation-id> --reason "container missing image"
```

The platform cancels any in-flight dispatch on every participating agent, removes the active-conversation pointer, emits a `ThreadClosed` activity event correlated to the conversation, and promotes the next pending conversation. The verb is idempotent — closing an unknown id is a safe no-op — so it's fine to script as a recovery step. Failures translate via `ProblemDetails`, mirroring the `POST /api/v1/conversations/{id}/close` HTTP surface.

### Inbox: Things Awaiting You

The inbox is the human-facing "things pointed at me that I have not responded to" surface. A conversation shows up here when an agent (or unit) has delivered a message to your `human://` address and no other participant has observed a follow-up message after that point; it drops off as soon as you respond. Trailing observability events on the conversation (state changes from dispatch teardown, cost emissions) do not affect the inbox.

```
spring inbox list                              # conversations awaiting a reply from you
spring inbox show <conversation-id>            # open the pending thread
spring inbox respond <conversation-id> "Approved — proceed."
spring inbox respond <conversation-id> --to agent://engineering-team/ada "Redirect the reply."
```

`respond` is a thin wrapper over `spring conversation send --conversation <id>` — it resolves the pending ask's sender automatically so the common case ("reply to whoever asked") needs no address.

## Agent Status

### Check All Agents in a Unit

```
spring agent status --unit engineering-team
```

Shows each agent's current state: idle, active (with conversation details), or suspended.

### Check a Specific Agent

```
spring agent status ada --unit engineering-team
```

Shows detailed status: current conversation, pending conversations, recent activity, memory summary.

## Cost Tracking

### Analytics (Costs, Throughput, Wait Times)

`spring analytics` is the current CLI surface for operational rollups. All
three verbs accept a shared `--window` flag (`24h`, `7d`, `30d`, `90d`, ...).

```
# Costs over a window — tenant, unit, or agent scoped.
spring analytics costs --window 7d
spring analytics costs --window 30d --unit engineering-team
spring analytics costs --window 24h --agent ada

# Throughput (messages / turns / tool calls) per source.
spring analytics throughput --window 7d
spring analytics throughput --window 30d --unit engineering-team
spring analytics throughput --window 7d --agent ada

# Wait-time rollups. Durations (idle / busy / waiting-for-human) are computed
# by pairing consecutive StateChanged lifecycle transitions; the `transitions`
# column still reports the raw StateChanged event count for the window.
spring analytics waits --window 7d --agent ada
```

`spring cost summary` continues to work as a deprecated alias for
`spring analytics costs`; the help text flags the deprecation. New scripts
should use the `analytics` verb.

### Budgets

```
# Tenant / unit / agent budgets all flow through the same verb.
spring cost set-budget --scope tenant --amount 50 --period monthly
spring cost set-budget --scope unit --target engineering-team --amount 20 --period weekly
spring cost set-budget --scope agent --target ada --amount 5 --period daily
```

`--period` accepts `daily`, `weekly`, or `monthly`. The server stores a daily
value; weekly / monthly amounts are normalised locally (`amount / 7` and
`amount / 30` respectively) so the portal's "Edit budget" action and the CLI
agree on what "$50 monthly" means.

## Web Dashboard

Open the web dashboard for a graphical view:

```
spring dashboard
```

The dashboard provides:

- Real-time activity feeds for all units and agents
- Agent status cards with current work and queue depth
- Cost graphs and budget tracking
- Conversation history and detail views
- Workflow progress visualization

## Notifications

Notifications are configured per-human in the unit definition:

```
spring unit humans add engineering-team savasp --permission owner --notifications slack,email
```

Notification events include:

- Agent errors and escalations
- Workflow steps requiring approval
- Cost budget alerts
- Conversation completions (configurable)

## Tips

- **Use `spring activity stream`** during active work to watch agents in real-time
- **Use `spring agent status`** for a quick check of what's happening
- **Use `spring analytics costs`** regularly to track spending (or the
  deprecated `spring cost summary` alias)
- **Use the dashboard** for a comprehensive overview when managing multiple units

## See it in action

Two fast e2e scenarios cover the read-side surfaces this guide depends on:

- [`fast/16-cost-api-shape.sh`](../../../tests/e2e/scenarios/fast/16-cost-api-shape.sh) — asserts `/api/v1/costs/{agents,units,tenant}` return well-formed `CostSummary` payloads with zero counters for fresh entities and honour explicit `from`/`to` windows. The shape every cost-reading surface (`spring cost summary`, portal Costs tab, dashboard) relies on.
- [`fast/17-activity-query-filters.sh`](../../../tests/e2e/scenarios/fast/17-activity-query-filters.sh) — asserts `source`, `eventType`, `severity`, and `pageSize` on `/api/v1/activity` all narrow results correctly. The `spring activity list` CLI and the portal activity page both query through this endpoint.

See [Runnable Examples](examples.md) for the full catalogue.
