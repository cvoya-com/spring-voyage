# Workflow Agent Image Sample

This sample is a deterministic workflow runtime image that uses `Cvoya.Spring.AgentSdk` to delegate one inbound turn to a direct child. It reads the inbound message from stdin, reads the dispatcher callback details from environment variables, chooses a child by keyword matching, calls `DelegateAsync`, and writes the child's result to stdout.

Required environment variables:

| Variable | Purpose |
| --- | --- |
| `SPRING_CALLBACK_URL` | Dispatcher base URL for SDK callbacks. |
| `SPRING_CALLBACK_TOKEN` | Launch-time bearer token injected by the launcher. Used as a fallback when the inbound message has no `message.metadata.callbackToken`. |
| `SPRING_THREAD_ID` | Current dispatcher thread id. |
| `SPRING_CHILD_0` | First direct child address, selected when the message contains `code`. |
| `SPRING_CHILD_1` | Second direct child address, selected otherwise. |

Build the image from the repository root:

```bash
docker build -f samples/workflow-agent-image/Dockerfile -t sv-workflow-sample .
```

Run it locally by pointing the callback variables at a running dispatcher and passing direct child addresses:

```bash
printf 'write some code' | docker run --rm -i \
  -e SPRING_CALLBACK_URL=http://host.docker.internal:5104 \
  -e SPRING_CALLBACK_TOKEN="$SPRING_CALLBACK_TOKEN" \
  -e SPRING_THREAD_ID="$SPRING_THREAD_ID" \
  -e SPRING_CHILD_0=agent:aaaaaaaa000000000000000000000001 \
  -e SPRING_CHILD_1=agent:aaaaaaaa000000000000000000000002 \
  sv-workflow-sample
```

Persistent containers should pass the raw inbound A2A `message/send` params to `SpringAgent.FromEnvironment(inboundMessageBody)`. When that body includes `message.metadata.callbackToken`, the SDK uses it for the current turn instead of the launch-time `SPRING_CALLBACK_TOKEN`.
