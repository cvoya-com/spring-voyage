# Cvoya.Spring.AgentRuntimes.Claude

Claude (Anthropic Claude Code CLI + Anthropic Platform API) agent
runtime. Pluggable `IAgentRuntime` implementation that ships as part of
the Spring Voyage open-source core. Originally migrated under issue #679
(which folded in the host-CLI dependency fix from #668); since #941 the
runtime's validation runs as an in-container probe plan via
`GetProbeSteps` — host-side shelling out is forbidden.

## What this project is

A self-contained drop-in for the agent-runtime plugin contract defined
in [`src/Cvoya.Spring.Core/AgentRuntimes/`](../Cvoya.Spring.Core/AgentRuntimes/README.md):

- `Id = "claude"` — stable identifier persisted in tenant installs and
  unit bindings.
- `ToolKind = "claude-code-cli"` — execution-tool group.
- `DisplayName = "Claude (Claude Code CLI + Anthropic API)"`.
- Validates both Anthropic Platform API keys (`sk-ant-api…`) and
  Claude.ai OAuth tokens from `claude setup-token` (`sk-ant-oat…`).
- Defaults its model catalog from the embedded
  [`agent-runtimes/claude/seed.json`](agent-runtimes/claude/seed.json).
- `GetProbeSteps` emits a `VerifyingTool` step that runs
  `claude --version` inside the chosen container image; a missing
  binary produces a precise `ToolUnavailable` error without shelling
  out on the host.

## Supported credential formats

| Prefix          | Format                                | Validation path                                                                                              |
|-----------------|---------------------------------------|---------------------------------------------------------------------------------------------------------------|
| `sk-ant-api…`   | Anthropic Platform API key            | `claude --bare -p` when the CLI is reachable; falls back to a REST `GET /v1/models` against `api.anthropic.com`. |
| `sk-ant-oat…`   | Claude.ai OAuth token                 | `claude --bare -p` only — the Anthropic REST endpoint rejects OAuth tokens, so the CLI is mandatory for this format. |

Validation outcomes map onto the `CredentialValidationStatus` enum from
the core contract:

- `Valid` — the credential is live (CLI returned success, or REST `GET /v1/models` returned 2xx).
- `Invalid` — Anthropic returned 401 / 403, or the credential format is unrecognized.
- `NetworkError` — DNS / TLS / timeout / 5xx, or the CLI itself was not reachable when validating an OAuth token.

## Runtime-image contract

The runtime requires the `claude` CLI binary on `PATH` inside its own
container image. The `UnitValidationWorkflow`'s `VerifyingTool` step
runs `claude --version` and reports any failure as a
`UnitValidationError` with code `ToolUnavailable`, so a missing CLI
produces a clear validation-time error rather than a cryptic
credential-validation failure downstream. Unlike the other OSS runtimes
(OpenAI, Google, Ollama) the Claude runtime uses the `claude` CLI —
not `curl` — for the `ValidatingCredential` step, so an image that
includes `claude` satisfies the contract. **This closes #668.**

## Updating the model list

Edit [`agent-runtimes/claude/seed.json`](agent-runtimes/claude/seed.json)
in the same PR that updates any other surface that references model
ids. The schema is documented in
[`src/Cvoya.Spring.Core/AgentRuntimes/README.md`](../Cvoya.Spring.Core/AgentRuntimes/README.md#seed-file-schema).
The default model (the `defaultModel` field) MUST be present in the
`models` array — the seed loader rejects mismatches at construction time
to prevent shipping a runtime that recommends a model it does not list.

The seed file is embedded into the assembly (see the `EmbeddedResource`
entry in `Cvoya.Spring.AgentRuntimes.Claude.csproj`); rebuilding picks
up the new contents. No on-disk lookup at boot.

## Wiring it in

Hosts register the runtime via the DI extension:

```csharp
using Cvoya.Spring.AgentRuntimes.Claude.DependencyInjection;

builder.Services
    .AddCvoyaSpringCore()
    .AddCvoyaSpringDapr(builder.Configuration)
    .AddCvoyaSpringAgentRuntimeClaude(); // ← here
```

Every registration uses `TryAdd*`, so a private cloud host that
pre-registers a tenant-scoped subclass of `ClaudeAgentRuntime` (or a
fully replacement `IAgentRuntime` with id `claude`) keeps its custom
implementation.

## Out of scope

- Codex, Gemini, OpenAI, and dapr-agent runtime migrations live in
  sibling projects (`Cvoya.Spring.AgentRuntimes.<Name>`).
