# Messaging and Interaction

This guide covers how to send messages to agents and units, read conversations, and interact with the system.

## Sending Messages

### To a Specific Agent

```
spring message send agent://engineering-team/ada "Review PR #42"
```

### To a Unit

Sending to a unit routes through the unit's orchestration strategy, which decides which member handles it:

```
spring message send agent://engineering-team "Implement the login feature described in issue #15"
```

### To a Role (Multicast)

Send to all agents with a specific role:

```
spring message send role://engineering-team/backend-engineer "New coding standards are in effect"
```

### With a Conversation ID

To add a message to an existing conversation:

```
spring message send agent://engineering-team/ada "Actually, also check the error handling" --conversation <id>
```

## Reading Conversations

### List Conversations for an Agent

```
spring conversation list --agent ada --unit engineering-team
```

### Read a Specific Conversation

```
spring conversation read <conversation-id>
```

### Follow a Conversation in Real-Time

```
spring conversation follow <conversation-id>
```

This streams new messages as they arrive, including agent responses and activity events.

## Address Formats

You can address entities in multiple ways:

| Format | Example |
|--------|---------|
| Path (within tenant) | `agent://engineering-team/ada` |
| Path (nested) | `agent://engineering-team/backend-team/ada` |
| Path (unit itself) | `agent://engineering-team` |
| Role (multicast) | `role://engineering-team/backend-engineer` |
| Direct (UUID) | `agent://@f47ac10b-58cc-4372-a567-0e02b2c3d479` |
| Human | `human://engineering-team/savasp` |
| Connector | `connector://engineering-team/github` |

## Workflow Interaction

### View Running Workflows

```
spring workflow list --unit engineering-team
spring workflow status <workflow-id>
```

### Approve Human-in-the-Loop Steps

When a workflow pauses for human approval:

```
spring workflow approve <workflow-id> --step <step-name>
spring workflow reject <workflow-id> --step <step-name> --reason "Need more tests"
```

## Escalations

When an agent escalates an issue, you'll receive a notification via your configured channels (Slack, email, dashboard). You can respond directly:

```
spring message send agent://engineering-team/ada "Proceed with approach B" --conversation <escalation-conversation-id>
```

## Tips

- **Use the unit address** when you don't know which specific agent should handle the work. The unit's orchestration strategy will route it.
- **Use agent addresses** when you want a specific agent to do the work.
- **Use role addresses** for broadcast communication to all agents with a specific role.
- **Conversation IDs** let you add context to in-progress work. The agent receives your message at its next checkpoint.
