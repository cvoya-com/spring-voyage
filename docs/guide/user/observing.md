# Observing Activity

This guide covers how to monitor agent activity, track costs, and use the dashboard.

## Activity Streams

### List Unit Activity

```
spring activity list --source unit:<id> --limit 50
```

This lists recent activity events from the unit and its members. You'll see:

- Messages sent and received
- Decisions being made
- Errors and warnings
- Tool calls and results
- Cost events

### Stream Activity in Real Time

`spring activity tail` streams activity events live over Server-Sent Events:

```
spring activity tail                            # tenant-wide
spring activity tail --unit engineering-team    # one unit and its descendants
spring activity tail --source agent:<id>        # one agent
```

Press `Ctrl+C` to stop streaming. `activity tail` accepts `--kind`, `--severity`, `--from`, and `--json`.

### Filter Listed Activity

```
spring activity list --source unit:<id> --severity Warning
spring activity list --source unit:<id> --type DecisionMade --limit 20
```

`activity list` filters by `--source`, `--type`, `--severity`, and `--limit`.

## Threads and Inbox

Activity is the raw, chronological log; **threads** are the narrative view of one participant set's shared exchanges. Both surfaces share the same underlying event store — a thread is the ordered subset of activity that belongs to one `ThreadId`.

### List and Show Threads

```
spring thread list
spring thread list --unit engineering-team
spring thread list --agent ada
spring thread list --participant human:f47ac10b58cc4372a5670e02b2c3d479
spring thread show <thread-id>
```

`list` prints one row per thread. Filters narrow by unit, agent, or participant address. `show` prints the thread header followed by the ordered event timeline. Both accept `--output json`.

### Post Into an Existing Thread

To post a new message into a thread an agent is already working on, use either of the equivalent forms:

```
spring thread send --thread <id> agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 "Looks good — ship it."
spring message send agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 "Looks good — ship it." --thread <id>
```

Both deliver a one-way domain message on the thread; pick whichever reads better in the surrounding script.

### Inbox: Things Awaiting You

The inbox is the human-facing "things pointed at me that I have not responded to" surface. A thread shows up here when an agent (or unit) has delivered a message to your `human:` address and you have not yet responded; it drops off as soon as you respond.

```
spring inbox list                         # threads awaiting a reply from you
spring inbox show <thread-id>             # open the pending thread
spring inbox respond <thread-id> "Approved — proceed."
spring inbox respond <thread-id> --to agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 "Direct the message elsewhere."
```

`respond` resolves the pending ask's sender automatically so the common case ("reply to whoever asked") needs no address.

## Agent Status

### Check All Agents in a Unit

```
spring agent status --unit engineering-team
```

Shows each agent's current state and per-thread activity.

### Check a Specific Agent

```
spring agent status ada --unit engineering-team
```

Shows detailed status: in-flight turns, recent activity, and a memory summary.

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

## Web Portal

The web portal gives a graphical view of the same data — open it in a browser
at the deployment's configured URL. It provides:

- Real-time activity feeds for all units and agents
- Agent status cards with current work
- Cost graphs and budget tracking
- Thread (engagement) history and detail views

See [Web Portal Walkthrough](portal.md) for the page-by-page reference.

## Notifications

Notification subscriptions are configured per-human on the unit. The `humans`
ACL verb sets them when granting a permission:

```
spring unit humans add engineering-team <identity> --permission owner --notifications slack,email
```

Notification events include:

- Agent errors and escalations
- Steps requiring approval
- Cost budget alerts
- Thread completions (configurable)

## Tips

- **Use `spring activity tail`** during active work to watch agents in real time
- **Use `spring activity list`** to scan recent history with filters
- **Use `spring agent status`** for a quick check of what's happening
- **Use `spring analytics costs`** regularly to track spending (or the
  deprecated `spring cost summary` alias)

## See it in action

Two `pool: fast` CLI scenarios cover the read-side surfaces this guide depends on:

- [`cost/cost-api-shape.sh`](../../../tests/e2e/cli/scenarios/cost/cost-api-shape.sh) — asserts `/api/v1/costs/{agents,units,tenant}` return well-formed `CostSummary` payloads with zero counters for fresh entities and honour explicit `from`/`to` windows. The shape every cost-reading surface (`spring cost summary`, portal Costs tab, dashboard) relies on.
- [`activity/activity-query-filters.sh`](../../../tests/e2e/cli/scenarios/activity/activity-query-filters.sh) — asserts `source`, `eventType`, `severity`, and `pageSize` on `/api/v1/activity` all narrow results correctly. The `spring activity list` CLI and the portal activity page both query through this endpoint.

See [Runnable Examples](examples.md) for the full catalogue.
