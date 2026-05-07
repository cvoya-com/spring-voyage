// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for OpenAI Codex containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>AGENTS.md</c> — the assembled system prompt (all four layers).
///         Codex reads this file as its instructions equivalent of Claude Code's <c>CLAUDE.md</c>.</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token the Codex agent will dial.</item>
/// </list>
/// The dispatcher materialises this workspace on its own host filesystem and
/// bind-mounts it at <c>/workspace</c> inside the container — see issue #1042.
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Codex CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>codex</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must read <c>AGENTS.md</c> and <c>.mcp.json</c> from the <c>/workspace</c>
/// mount and honour the <c>OPENAI_API_KEY</c> environment variable for
/// authentication with the OpenAI API.
/// </para>
/// </summary>
public class CodexLauncher(
    IAgentRuntimeRegistry runtimeRegistry,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IAgentRuntimeLauncher
{
    /// <summary>
    /// Runtime id whose credential the Codex launcher injects. Codex uses
    /// the OpenAI Platform API, so the credential is the OpenAI API key
    /// (the same secret slot the Spring Voyage runtime uses for
    /// <c>provider: openai</c>).
    /// </summary>
    internal const string OpenAiRuntimeId = "openai";

    internal const string WorkspaceMountPath = "/workspace";
    private readonly ILogger _logger = loggerFactory.CreateLogger<CodexLauncher>();

    /// <inheritdoc />
    /// <remarks>
    /// #1732: keyed on <c>codex-cli</c> — the canonical tool kind a future
    /// Codex agent runtime would declare. No <c>IAgentRuntime</c> currently
    /// resolves to this launcher; it ships ahead of the runtime.
    /// </remarks>
    public string Kind => "codex-cli";

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["spring-voyage"] = new
                {
                    type = "http",
                    url = context.McpEndpoint,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {context.McpToken}"
                    }
                }
            }
        };

        var workspaceFiles = new Dictionary<string, string>
        {
            ["AGENTS.md"] = context.Prompt,
            [".mcp.json"] = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true })
        };

        _logger.LogInformation(
            "Prepared Codex workspace request ({FileCount} files) for agent {AgentId} thread {ThreadId}",
            workspaceFiles.Count, context.AgentId, context.ThreadId);

        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN are
        // removed — AgentContextBuilder now emits the D1-canonical equivalents
        // (SPRING_AGENT_ID, SPRING_MCP_URL, SPRING_MCP_TOKEN) for every launcher.
        // SPRING_THREAD_ID, SPRING_SYSTEM_PROMPT have no D1-spec equivalent and
        // are retained here as launcher-specific vars.
        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt,
            // D3c: canonical path where the per-agent workspace volume is
            // mounted (D1 spec § 2.2.1, `SPRING_WORKSPACE_PATH`).
            [AgentVolumeManager.WorkspacePathEnvVar] = AgentVolumeManager.WorkspaceMountPath,
        };

        // #1714 step 2: inject the OpenAI API key into OPENAI_API_KEY.
        // Codex uses the OpenAI Platform API; its credential schema is
        // a single accepted shape (sk-… API keys) so there is no
        // shape-branching at the launcher.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        return new AgentLaunchSpec(
            WorkspaceFiles: workspaceFiles,
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath);
    }

    private async Task ResolveRuntimeCredentialAsync(
        AgentLaunchContext context,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
        var runtime = runtimeRegistry.Get(OpenAiRuntimeId)
            ?? throw new SpringException(
                $"OpenAI agent runtime is not registered (required by the Codex launcher). " +
                $"Install the Cvoya.Spring.AgentRuntimes.OpenAI package or remove `tool: codex` from this unit's manifest.");

        Guid? agentGuid = Guid.TryParse(context.AgentId, out var parsedAgentId)
            ? parsedAgentId
            : null;
        Guid? unitGuid = Guid.TryParse(context.UnitId, out var parsedUnitId)
            ? parsedUnitId
            : null;

        // Per-call scope so the scoped resolver works from this singleton.
        await using var scope = scopeFactory.CreateAsyncScope();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();
        var resolution = await credentialResolver.ResolveAsync(
            OpenAiRuntimeId, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            throw new SpringException(
                $"Codex agent runtime requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Generate an API key at https://platform.openai.com/api-keys and store it under '{resolution.SecretName}', " +
                $"or configure via the Tenant defaults panel.");
        }

        if (!runtime.IsCredentialFormatAccepted(resolution.Value!, CredentialDispatchPath.AgentRuntime))
        {
            throw new SpringException(
                $"Codex agent runtime did not accept the configured '{resolution.SecretName}' value at scope " +
                $"'{resolution.Source}'. The OpenAI Platform CLI path requires an OpenAI API key (sk-…).");
        }

        envVars[runtime.CredentialEnvVar] = resolution.Value!;

        _logger.LogInformation(
            "Codex credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, runtime.CredentialEnvVar, context.AgentId);
    }
}