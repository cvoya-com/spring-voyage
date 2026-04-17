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

### Cost Summary

```
spring cost summary --unit engineering-team --period today
spring cost summary --unit engineering-team --period this-month
spring cost summary --tenant --period last-30d
```

### Cost by Agent

```
spring cost breakdown --unit engineering-team --period today
```

Shows cost per agent, broken down by work vs. initiative.

### Budget Status

```
spring cost budget --unit engineering-team
spring cost budget --tenant
```

Shows current spending against configured limits.

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
- **Use `spring cost summary`** regularly to track spending
- **Use the dashboard** for a comprehensive overview when managing multiple units

## See it in action

Two fast e2e scenarios cover the read-side surfaces this guide depends on:

- [`fast/16-cost-api-shape.sh`](../../tests/e2e/scenarios/fast/16-cost-api-shape.sh) — asserts `/api/v1/costs/{agents,units,tenant}` return well-formed `CostSummary` payloads with zero counters for fresh entities and honour explicit `from`/`to` windows. The shape every cost-reading surface (`spring cost summary`, portal Costs tab, dashboard) relies on.
- [`fast/17-activity-query-filters.sh`](../../tests/e2e/scenarios/fast/17-activity-query-filters.sh) — asserts `source`, `eventType`, `severity`, and `pageSize` on `/api/v1/activity` all narrow results correctly. The `spring activity list` CLI and the portal activity page both query through this endpoint.

See [Runnable Examples](examples.md) for the full catalogue.
