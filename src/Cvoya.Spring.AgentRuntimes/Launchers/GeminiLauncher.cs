// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntimeLauncher"/> for Gemini CLI containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>GEMINI.md</c> — the assembled system prompt (all four layers).
///         Gemini CLI reads this file as its instructions file.</item>
///   <item><c>.gemini/settings.json</c> — MCP server endpoint + bearer token the Gemini agent will dial.</item>
/// </list>
/// The dispatcher materialises this workspace on its own host filesystem and
/// bind-mounts it at <c>/workspace</c> inside the container — see issue #1042.
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Gemini CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>gemini</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must run Gemini from the <c>/workspace</c> mount so it reads
/// <c>GEMINI.md</c> and project-scoped <c>.gemini/settings.json</c>, and honour
/// the <c>GOOGLE_API_KEY</c> environment variable for authentication with the
/// Google AI API.
/// </para>
/// </summary>
public class GeminiLauncher(
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory,
    IAgentCallbackEnvironmentBuilder? callbackEnvironmentBuilder = null) : IAgentRuntimeLauncher
{
    /// <summary>
    /// Runtime id whose credential the Gemini launcher injects. Gemini
    /// CLI uses the Google Generative Language API, so the credential is
    /// the Google AI Studio API key (the same secret slot the Spring
    /// Voyage runtime uses for <c>provider: google</c>).
    /// </summary>
    /// <summary>
    /// Provider id this launcher consumes from the catalogue's
    /// <c>(provider, authMethod)</c> edge per ADR-0038. The Gemini agent
    /// runtime accepts the Google provider with the API-key auth method.
    /// </summary>
    internal const string ProviderId = "google";

    /// <summary>Container env var the Gemini CLI reads its API key from.</summary>
    internal const string CredentialEnvVar = "GOOGLE_API_KEY";

    internal const string WorkspaceMountPath = "/workspace";
    internal const string GeminiSettingsPath = ".gemini/settings.json";

    private const string SpringVoyageMcpServerName = "spring-voyage";
    private const string SpringOrchestrationMcpServerName = "spring-orchestration";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger _logger = loggerFactory.CreateLogger<GeminiLauncher>();

    /// <inheritdoc />
    /// <remarks>
    /// Matches the <c>launcher</c> field on the runtime catalogue's
    /// <c>gemini</c> entry per ADR-0038 decision 2.
    /// </remarks>
    public string Kind => LauncherIds.GeminiCli;

    /// <inheritdoc />
    /// <remarks>
    /// Verify-tool baseline only. Per-runtime credential and model probes
    /// migrate alongside the manifest reshape in PR-1b — see follow-up
    /// captured in the Chunk 2a final report; PR-1b regenerates these
    /// against the new <c>(runtime, model)</c> domain.
    /// </remarks>
    public IReadOnlyList<ProbeStep> GetProbeSteps(ModelProviderInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new[]
        {
            new ProbeStep(
                Step: UnitValidationStep.VerifyingTool,
                Args: new[] { "gemini", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: TimeSpan.FromSeconds(10),
                InterpretOutput: (exit, _, stderr) => exit == 0
                    ? StepResult.Succeed()
                    : StepResult.Fail(
                        UnitValidationCodes.ToolMissing,
                        $"`gemini --version` exited with code {exit}. {stderr}".TrimEnd())),
        };
    }

    /// <inheritdoc />
    public async Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        // #1322: SPRING_AGENT_ID, SPRING_MCP_ENDPOINT, SPRING_AGENT_TOKEN are
        // removed — AgentContextBuilder now emits the D1-canonical equivalents
        // (SPRING_AGENT_ID, SPRING_MCP_URL, SPRING_MCP_TOKEN) for every launcher.
        // SPRING_THREAD_ID, SPRING_SYSTEM_PROMPT have no D1-spec equivalent and
        // are retained here as launcher-specific vars.
        // ADR-0041 / #2096: when concurrent_threads is on, prepend the
        // shared launcher guard to the assembled prompt so the model is
        // told (in the system prompt) not to invoke long-running watchers,
        // bind fixed ports, or mutate shared global state. Composes with
        // the user's prompt — never replaces it.
        var prompt = LauncherPromptFragments.Compose(context.Prompt, context.ConcurrentThreads);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_SYSTEM_PROMPT"] = prompt,
            // D3c: canonical path where the per-agent workspace volume is
            // mounted (D1 spec § 2.2.1, `SPRING_WORKSPACE_PATH`).
            [AgentWorkspaceContract.WorkspacePathEnvVar] = AgentWorkspaceContract.WorkspaceMountPath,
        };

        LauncherCallbackEnvironment.Add(callbackEnvironmentBuilder, context, envVars);

        var workspaceFiles = new Dictionary<string, string>
        {
            ["GEMINI.md"] = prompt,
            [GeminiSettingsPath] = JsonSerializer.Serialize(BuildGeminiSettings(context, envVars), JsonOptions)
        };

        _logger.LogInformation(
            "Prepared Gemini workspace request ({FileCount} files) for agent {AgentId} thread {ThreadId}",
            workspaceFiles.Count, context.AgentId, context.ThreadId);

        // #1714 step 2: inject the Google AI Studio API key into
        // GOOGLE_API_KEY. Gemini's credential schema is a single accepted
        // shape, so there is no shape-branching at the launcher.
        await ResolveRuntimeCredentialAsync(context, envVars, cancellationToken);

        return new AgentLaunchSpec(
            WorkspaceFiles: workspaceFiles,
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath);
    }

    private static object BuildGeminiSettings(
        AgentLaunchContext context,
        IReadOnlyDictionary<string, string> envVars)
    {
        var mcpServers = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SpringVoyageMcpServerName] = new
            {
                httpUrl = context.McpEndpoint,
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {context.McpToken}"
                }
            }
        };

        if (context.OrchestrationTools is { Length: > 0 } orchestrationTools)
        {
            mcpServers[SpringOrchestrationMcpServerName] = new
            {
                httpUrl = LauncherCallbackEnvironment.BuildOrchestrationMcpUrl(envVars),
                headers = new Dictionary<string, string>
                {
                    ["Authorization"] =
                        $"Bearer {envVars[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]}"
                },
                includeTools = orchestrationTools.Select(tool => ToWireName(tool.Name)).ToArray()
            };
        }

        return new
        {
            mcpServers
        };
    }

    private static string ToWireName(OrchestrationToolName name) =>
        name switch
        {
            OrchestrationToolName.ListChildren => "list_children",
            OrchestrationToolName.InspectChild => "inspect_child",
            OrchestrationToolName.DelegateToChild => "delegate_to_child",
            OrchestrationToolName.FanoutToChildren => "fanout_to_children",
            OrchestrationToolName.QueryChildStatus => "query_child_status",
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown orchestration tool name.")
        };

    private async Task ResolveRuntimeCredentialAsync(
        AgentLaunchContext context,
        IDictionary<string, string> envVars,
        CancellationToken cancellationToken)
    {
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
        // ADR-0038 (#1770): the resolver is keyed on (provider, authMethod).
        // The Gemini runtime consumes Google via API key.
        var resolution = await credentialResolver.ResolveAsync(
            ProviderId, AuthMethod.ApiKey, agentGuid, unitGuid, cancellationToken);

        if (resolution.Source is LlmCredentialSource.NotFound or LlmCredentialSource.Unreadable
            || string.IsNullOrEmpty(resolution.Value))
        {
            throw new SpringException(
                $"Gemini agent runtime requires secret '{resolution.SecretName}' but no value resolved at " +
                $"agent, unit, parent-unit chain, or tenant scope. " +
                $"Generate an API key at https://aistudio.google.com/apikey and store it under '{resolution.SecretName}', " +
                $"or configure via the Tenant defaults panel.");
        }

        // Google AI Studio API keys are typically AIza-prefixed but the
        // launcher accepts any non-empty value here — Google's API
        // returns 401 on bad format, which the credential-health
        // watchdog already records.
        envVars[CredentialEnvVar] = resolution.Value!;

        _logger.LogInformation(
            "Gemini credential resolved from {Source} into {EnvVar} for agent {AgentId}",
            resolution.Source, CredentialEnvVar, context.AgentId);
    }
}
