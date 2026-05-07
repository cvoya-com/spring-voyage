// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for the Spring Voyage Agent container. Sets the
/// environment variables the Python Dapr Agent expects: MCP endpoint/token,
/// LLM provider/model, and the assembled system prompt. The dispatcher
/// materialises an empty per-invocation workspace and bind-mounts it at
/// <c>/workspace</c> — the Dapr Agent currently consumes its prompt via
/// <c>SPRING_SYSTEM_PROMPT</c>, but the workspace mount keeps the launch
/// shape uniform across tool launchers.
///
/// Unlike <see cref="ClaudeCodeLauncher"/> the Dapr Agent is an A2A-native
/// service and does not need a sidecar adapter — it exposes the A2A endpoint
/// directly. The dispatcher reaches the agent on the container's
/// <c>AGENT_PORT</c> (default 8999).
///
/// PR 4 of the #1087 series wires the launcher to BYOI conformance path 3:
/// the spec sets a non-empty <see cref="AgentLaunchSpec.Argv"/> that bypasses
/// the agent-base bridge entirely and hands control directly to the Python
/// process that already speaks A2A natively.
/// </summary>
public class SpringVoyageAgentLauncher(
    IOptions<OllamaOptions> ollamaOptions,
    IAgentRuntimeRegistry runtimeRegistry,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IAgentRuntimeLauncher
{
    internal const string WorkspaceMountPath = "/workspace";

    /// <summary>Default A2A port the Dapr Agent listens on.</summary>
    internal const int DefaultAgentPort = 8999;

    /// <summary>
    /// Argv vector that bypasses the agent-base bridge and starts the Dapr
    /// Agent process directly. Matches the CMD declared by
    /// <c>agents/spring-voyage-agent/Dockerfile</c>. BYOI conformance path 3.
    /// </summary>
    /// <remarks>
    /// Issue #1106 verified (2026-04): the upstream <c>dapr-agents 1.0.1</c>
    /// PyPI package does NOT publish a runnable A2A entrypoint module —
    /// <c>dapr_agents/__init__.py</c> exports <c>DurableAgent</c>,
    /// <c>AgentRunner</c>, chat clients, and helpers, but no
    /// <c>dapr_agents.a2a</c> module. The A2A surface is provided by
    /// <c>a2a-sdk[http-server]</c>; agents wire their own ASGI app and
    /// expose it via uvicorn (see <c>agents/spring-voyage-agent/agent.py</c> +
    /// <c>agents/spring-voyage-agent/a2a_server.py</c>). If upstream ever adds a
    /// runnable A2A module, this argv can be swapped for
    /// <c>python -m dapr_agents.&lt;module&gt;</c> without changing the
    /// launcher contract.
    /// </remarks>
    internal static readonly string[] DefaultSpringVoyageAgentArgv = ["python", "agent.py"];

    private readonly ILogger _logger = loggerFactory.CreateLogger<SpringVoyageAgentLauncher>();

    /// <summary>
    /// Tool-kind identifier for this launcher. Matches the
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime.Kind"/>
    /// declared by every runtime that dispatches through it
    /// (<c>openai</c>, <c>google</c>, <c>ollama</c> — all share
    /// <c>spring-voyage</c>).
    /// </summary>
    public const string ToolId = "spring-voyage";

    /// <inheritdoc />
    public string Kind => ToolId;

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Prepared Spring Voyage Agent launch request for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

        var opts = ollamaOptions.Value;

        // Provider / model selection is YAML-driven via AgentDefinition.Execution:
        // when the definition specifies execution.provider / execution.model those win.
        // Otherwise the launcher falls back to Ollama defaults so existing definitions
        // without the fields continue to work. These env vars map to the Dapr
        // Conversation component name ("llm-provider") and model metadata consumed by
        // the Python agent; changing provider is a YAML-only change (#480 acceptance).
        var provider = !string.IsNullOrWhiteSpace(context.Provider) ? context.Provider! : "ollama";
        var model = !string.IsNullOrWhiteSpace(context.Model)
            ? context.Model!
            : opts.DefaultModel ?? "llama3.2:3b";

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN are
        // removed — AgentContextBuilder now emits the D1-canonical equivalents
        // (SPRING_AGENT_ID, SPRING_MCP_URL, SPRING_MCP_TOKEN) for every launcher.
        //
        // #1327: SPRING_MODEL, SPRING_LLM_PROVIDER, SPRING_LLM_COMPONENT are
        // added to the D1 spec (§ 2.2.1) and emitted here as Dapr-agent-specific
        // vars. AgentContextBuilder emits SPRING_LLM_PROVIDER_URL for all launchers.
        // SPRING_LLM_COMPONENT remains launcher-specific (Dapr Conversation component
        // name) and is not part of the D1 spec.
        //
        // #1328: OLLAMA_ENDPOINT removed — conversation-ollama.yaml now reads
        // SPRING_LLM_PROVIDER_URL.
        //
        // SPRING_THREAD_ID and SPRING_SYSTEM_PROMPT have no D1-spec equivalents
        // and are retained as launcher-specific vars.
        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt,
            ["SPRING_MODEL"] = model,
            ["SPRING_LLM_PROVIDER"] = provider,
            // AGENT_PORT is the env var the in-container agent.py binds to
            // (see agents/spring-voyage-agent/Dockerfile). DAPR_AGENT_PORT is the
            // contract name introduced by issue #1097 — kept alongside
            // AGENT_PORT for back-compat with existing deployments while
            // PR 5 cuts the dispatcher over to the new field.
            ["AGENT_PORT"] = DefaultAgentPort.ToString(),
            ["DAPR_AGENT_PORT"] = DefaultAgentPort.ToString(),
            // The Python `dapr` SDK defaults its gRPC client deadline to
            // 60 s (`DAPR_API_TIMEOUT_SECONDS`); the Conversation Alpha2
            // unary call inherits that deadline. With ~58 MCP tool
            // schemas in the prompt, llama3.2:3b on CPU takes far longer
            // than 60 s to produce its first response, so the call hits
            // the deadline and returns `DEADLINE_EXCEEDED` /
            // `Received RST_STREAM with error code 8` before the agent's
            // loop can make progress. 600 s gives Ollama on a slow CPU
            // enough headroom for a single LLM turn while still bounding
            // a hung sidecar.
            ["DAPR_API_TIMEOUT_SECONDS"] = "600",
            // D3c: canonical path where the per-agent workspace volume is
            // mounted (D1 spec § 2.2.1, `SPRING_WORKSPACE_PATH`).
            [AgentVolumeManager.WorkspacePathEnvVar] = AgentVolumeManager.WorkspaceMountPath,
        };

        // #1328: OLLAMA_ENDPOINT removed. The Dapr Conversation component YAML
        // (conversation-ollama.yaml) now reads SPRING_LLM_PROVIDER_URL, which is
        // emitted by AgentContextBuilder for every launcher. OLLAMA_ENDPOINT is no
        // longer set here.

        // #1714: per-provider credential resolution + Conversation component
        // selection. The Spring Voyage runtime routes to one of N providers
        // (Anthropic / OpenAI / Google / Ollama) — the Conversation component
        // each provider ships pins both the API endpoint and the secret key
        // ref, so the launcher must (a) inject the resolved credential under
        // the provider's expected env-var name so daprd's local-env secret
        // store can read it, and (b) point the agent at the right component
        // by name via SPRING_LLM_COMPONENT.
        await ResolveProviderCredentialAsync(context, provider, envVars, cancellationToken);

        return new AgentLaunchSpec(
            WorkspaceFiles: new Dictionary<string, string>(),
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath,
            // Non-empty argv: skip the agent-base bridge ENTRYPOINT and
            // hand control directly to the Python process that already
            // speaks A2A on :8999. BYOI conformance path 3.
            Argv: DefaultSpringVoyageAgentArgv,
            // Dapr Agent receives messages via A2A, not stdin.
            StdinPayload: null);
    }

    /// <summary>
    /// Maps a provider id (the value the operator types into the unit's
    /// <c>execution.provider</c> field — <c>anthropic</c>, <c>openai</c>,
    /// <c>google</c>, <c>ollama</c>) to the runtime registry's stable id
    /// for that provider. The Anthropic provider routes through the
    /// <c>claude</c> runtime which owns the credential schema; the other
    /// providers map 1:1.
    /// </summary>
    internal static string MapProviderToRuntimeId(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => "claude",
        _ => provider.ToLowerInvariant(),
    };

    private async Task ResolveProviderCredentialAsync(
        AgentLaunchContext context,
        string provider,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        var runtimeId = MapProviderToRuntimeId(provider);
        var runtime = runtimeRegistry.Get(runtimeId);

        // Always pin the conversation component so the Python agent dials
        // the right Dapr Conversation YAML. The component-naming convention
        // is `conversation-<provider-id>` — set on every dispatch (including
        // Ollama, which has no credential to inject) so agent.py never
        // silently falls back to the legacy "llm-provider" default.
        envVars["SPRING_LLM_COMPONENT"] = $"conversation-{provider.ToLowerInvariant()}";

        if (runtime is null)
        {
            // The configured provider does not match a registered runtime.
            // The unit-validation workflow should have caught this already
            // — fail loudly so the operator sees an actionable error
            // instead of an in-container conversation timeout.
            throw new SpringException(
                $"Unit configured with provider '{provider}', but no agent runtime is registered with id '{runtimeId}'. " +
                $"Install the matching runtime package or fix the unit's execution.provider value.");
        }

        // Ollama has no credential to inject; the conversation-ollama.yaml
        // component carries a literal "ollama" key. Skip resolution.
        if (string.IsNullOrEmpty(runtime.CredentialSecretName)
            || string.IsNullOrEmpty(runtime.CredentialEnvVar))
        {
            return;
        }

        // The Spring Voyage runtime dispatches via Dapr Conversation REST.
        // Resolve the credential through the chain (agent → unit → tenant)
        // and inspect the shape against the REST path before injecting.
        Guid? agentGuid = Guid.TryParse(context.AgentId, out var parsedAgentId)
            ? parsedAgentId
            : null;
        Guid? unitGuid = Guid.TryParse(context.UnitId, out var parsedUnitId)
            ? parsedUnitId
            : null;

        // ILlmCredentialResolver is scoped (it composes the scoped
        // ISecretResolver/SpringDbContext); this launcher is a singleton,
        // so resolve through a per-call scope to honour DI lifetimes.
        await using var scope = scopeFactory.CreateAsyncScope();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();
        var resolution = await credentialResolver.ResolveAsync(
            runtimeId, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            throw new SpringException(
                $"Provider '{provider}' requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Configure via the Tenant defaults panel or `spring secret set --scope tenant {resolution.SecretName} <value>`.");
        }

        if (!runtime.IsCredentialFormatAccepted(resolution.Value!, CredentialDispatchPath.Rest))
        {
            // Strict per-path acceptance (#1714): the Spring Voyage runtime
            // routes `provider: anthropic` via REST, which only accepts
            // Anthropic Platform API keys (sk-ant-api…). OAuth tokens
            // (sk-ant-oat…) belong on the Claude agent-runtime path —
            // operator must either replace the secret with an API key or
            // switch the unit to `agent: claude`.
            throw new SpringException(
                $"Spring Voyage routes '{provider}' via the Dapr Conversation REST path, which does not accept " +
                $"the resolved credential's shape (secret '{resolution.SecretName}' from {resolution.Source}). " +
                $"Either replace the secret with a REST-compatible credential (e.g. an Anthropic Platform API key " +
                $"sk-ant-api…), or switch this agent to a runtime that accepts that shape (e.g. `agent: claude` " +
                $"for an Anthropic OAuth token).");
        }

        // The env-var name on the REST path is provider-specific and must match
        // both `dapr/components/delegated-spring-voyage-agent/conversation-<provider>.yaml`'s
        // secretKeyRef name AND `ContainerLifecycleManager.CredentialEnvVarsToPropagate`
        // — the daprd sidecar reads the secret from its own process env via the
        // local-env secret store. `runtime.CredentialEnvVar` is the runtime's CLI/
        // agent-runtime path env var (e.g. `CLAUDE_CODE_OAUTH_TOKEN` for Claude),
        // which is intentionally different from the REST path env var.
        var providerEnvVar = provider.ToLowerInvariant() switch
        {
            "anthropic" => "ANTHROPIC_API_KEY",
            "openai" => "OPENAI_API_KEY",
            "google" => "GOOGLE_API_KEY",
            _ => throw new SpringException(
                $"Spring Voyage launcher cannot map provider '{provider}' to a Conversation REST env var. " +
                $"Supported providers: anthropic, openai, google. Add the mapping (and a matching " +
                $"conversation-<provider>.yaml + ContainerLifecycleManager propagation entry) to extend.")
        };
        envVars[providerEnvVar] = resolution.Value!;
    }
}