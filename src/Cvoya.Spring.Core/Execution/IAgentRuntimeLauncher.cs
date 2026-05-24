// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// Describes the container-launch contract for one specific agent runtime.
/// Different runtimes (Claude Code, Codex, Gemini CLI, …) materialise
/// their per-invocation configuration differently, so each gets its own
/// launcher. The dispatcher selects the launcher whose <see cref="Kind"/>
/// matches the catalogue runtime's
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> id.
/// </summary>
/// <remarks>
/// <para>
/// Launchers no longer touch the local filesystem: they describe the workspace
/// they need (file contents keyed by relative path, plus the desired in-container
/// mount path) and let the dispatcher service materialise that workspace on its
/// own host filesystem. This is what allows the agent container's bind mount to
/// resolve to a real path the container runtime can see — see issue #1042.
/// </para>
/// <para>
/// Per ADR-0038, launchers are keyed on the catalogue runtime's
/// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/> id. The
/// dispatcher looks up the runtime by the agent's persisted
/// <c>execution.runtime</c> (i.e.
/// <see cref="AgentExecutionConfig.Runtime"/>) through
/// <see cref="Cvoya.Spring.Core.Catalog.IRuntimeCatalog.GetAgentRuntime"/>
/// and picks the launcher whose <see cref="Kind"/> matches the
/// runtime's <c>Launcher</c> field.
/// </para>
/// </remarks>
public interface IAgentRuntimeLauncher
{
    /// <summary>
    /// The launcher-strategy id this launcher handles. Must match the
    /// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/>
    /// field of every catalogue runtime that should dispatch through
    /// this launcher (multiple runtimes may share a launcher when they
    /// differ only in their LLM backend — e.g. <c>openai</c>,
    /// <c>google</c>, and <c>ollama</c> all share
    /// <c>spring-voyage-agent</c>).
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Builds the container-launch contract for one invocation. The returned
    /// <see cref="AgentLaunchSpec"/> describes the argv to run, the env vars
    /// to set, any extra volume mounts, and how the dispatcher should
    /// capture the agent's response. Under ADR-0055 the launcher no longer
    /// emits in-workspace files here — those move to
    /// <see cref="ContributeBundleAsync"/>. Launchers MUST NOT write to
    /// the local filesystem.
    /// </summary>
    Task<AgentLaunchSpec> PrepareAsync(AgentLaunchContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the launcher's contribution to the agent's bootstrap bundle
    /// (ADR-0055 §3). The agent-sidecar pulls the assembled bundle from the
    /// worker on container start and writes every file under the per-member
    /// workspace mount; the platform-authoritative subset is pinned per-turn
    /// via the sidecar's integrity check.
    /// </summary>
    /// <remarks>
    /// This is the one-and-only home for the launcher's in-workspace files
    /// (the runtime's system-prompt file and MCP config file). The method
    /// takes no per-turn / per-message context — the bundle is launch-time
    /// and re-pulled on integrity-check mismatch; per-turn data rides the
    /// A2A wire (ADR-0055 §4).
    /// </remarks>
    Task<AgentBootstrapContribution> ContributeBundleAsync(
        AgentBootstrapContributionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the runtime-true prose the prompt assembler renders under
    /// the platform-injected <c>## Container and workspace</c> section
    /// (#2682). Describes the per-runtime container surface — workspace
    /// path env var, CLI tool baseline, session-storage env vars, MCP
    /// discovery — so authors do not have to repeat it in every agent's
    /// instructions. Synchronous and context-free: the prose is a
    /// launcher constant referencing env-var names (not values), so the
    /// bundle provider can call it before assembling the system prompt
    /// without round-tripping per-agent state. Return <c>null</c> when
    /// the runtime has no container/workspace concept (the A2A-native
    /// <c>spring-voyage-agent</c> launcher).
    /// </summary>
    string? GetWorkspacePromptFragment();

    /// <summary>
    /// Builds the declarative list of in-container probe commands the
    /// Dapr <c>ArtefactValidationWorkflow</c> should execute against the
    /// unit's chosen container image, after pulling the image and
    /// starting it. Per ADR-0038 the probe logic lives next to the
    /// launcher that actually runs the tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returning an empty list means the launcher has nothing to probe;
    /// the workflow treats that as "skip post-pull validation" and
    /// proceeds.
    /// </para>
    /// <para>
    /// <see cref="Cvoya.Spring.Core.Lifecycle.ArtefactValidationStep.PullingImage"/>
    /// is the dispatcher's concern and MUST NOT appear here.
    /// </para>
    /// </remarks>
    /// <param name="config">
    /// The tenant's stored install configuration. Implementations typically
    /// read <see cref="ModelProviderInstallConfig.DefaultModel"/> to target
    /// the model that the unit's binding will run, and
    /// <see cref="ModelProviderInstallConfig.BaseUrl"/> to override the
    /// provider endpoint.
    /// </param>
    /// <param name="credential">
    /// The raw credential to inject into the probe environment. Empty when
    /// the runtime requires no credential.
    /// </param>
    IReadOnlyList<ProbeStep> GetProbeSteps(ModelProviderInstallConfig config, string credential);
}

/// <summary>
/// Inputs the dispatcher hands to a launcher for a single invocation.
/// </summary>
/// <param name="AgentId">The agent id (for logging and prompt materialisation).</param>
/// <param name="ThreadId">The thread id being served.</param>
/// <param name="Prompt">The assembled system prompt (platform instructions + unit context + role-specific instructions).</param>
/// <param name="McpEndpoint">The URL the container should use to reach the MCP server.</param>
/// <param name="McpToken">The bearer token the container must present on MCP calls.</param>
/// <param name="TenantId">
/// The tenant identifier for the agent's execution context. Delivered to the
/// container as <c>SPRING_TENANT_ID</c> (D1 spec § 2.2.1).
/// </param>
/// <param name="UnitId">
/// Optional unit identifier. Delivered as <c>SPRING_UNIT_ID</c> when non-null.
/// </param>
/// <param name="ConcurrentThreads">
/// Resolved value of the agent / unit <c>concurrent_threads</c> policy flag
/// (D1 spec § 2.1, § 1.2.4). Delivered as <c>SPRING_CONCURRENT_THREADS</c>.
/// </param>
/// <param name="Provider">
/// Optional LLM provider selector from the agent's <see cref="AgentExecutionConfig.Provider"/>.
/// Launchers that front a Dapr Conversation runtime (e.g. the Dapr Agent) use
/// this to pin the component by name. <c>null</c> means "use launcher default".
/// Launchers that do not route through Dapr Conversation may ignore this field.
/// </param>
/// <param name="Model">
/// Optional model identifier from the agent's <see cref="AgentExecutionConfig.Model"/>.
/// <c>null</c> means "use launcher default".
/// </param>
/// <param name="AgentAddress">
/// Full address for the invoked runtime. Defaults to <c>agent:{AgentId}</c>
/// when omitted, but dispatchers should pass the inbound message target so
/// the OTLP resource attributes are stamped with the correct subject scheme.
/// </param>
/// <param name="CallbackThreadId">
/// Guid-shaped thread id stamped into the OTLP-ingest callback token.
/// Defaults to parsing <see cref="ThreadId"/> when omitted.
/// </param>
/// <param name="MessageId">
/// Inbound message id stamped into the OTLP-ingest callback token. Synthetic
/// launch paths that are not serving an inbound message supply their own
/// synthetic id.
/// </param>
/// <param name="SystemPromptMode">
/// Resolved system-prompt mode (#2691 / #2667). The agent → unit → default
/// cascade has already been applied at the dispatch site, so launchers
/// consume this value directly without further fallback. Defaults to
/// <see cref="Cvoya.Spring.Core.Catalog.SystemPromptMode.Append"/> for
/// launch paths that have not yet been wired through the cascade.
/// Launcher consumption lands in a sibling sub-issue; this PR only plumbs
/// the value through.
/// </param>
public record AgentLaunchContext(
    string AgentId,
    string ThreadId,
    string Prompt,
    string McpEndpoint,
    string McpToken,
    Guid TenantId,
    string? UnitId = null,
    bool ConcurrentThreads = true,
    string? Provider = null,
    string? Model = null,
    Address? AgentAddress = null,
    Guid? CallbackThreadId = null,
    Guid? MessageId = null,
    Cvoya.Spring.Core.Catalog.SystemPromptMode SystemPromptMode = Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

/// <summary>
/// Output of <see cref="IAgentRuntimeLauncher.PrepareAsync"/>. Pure data —
/// env vars, argv, and container-launch options. Per ADR-0055 the launcher
/// no longer emits in-workspace files here; the agent's
/// <c>CLAUDE.md</c> / <c>.mcp.json</c> / etc. ride the bundle returned by
/// <see cref="IAgentRuntimeLauncher.ContributeBundleAsync"/> and are
/// materialised by the agent-sidecar after pulling the bundle.
/// </summary>
/// <param name="EnvironmentVariables">Env vars the dispatcher must add to the container (on top of its own baseline).</param>
/// <param name="ExtraVolumeMounts">
/// Additional volume-mount specs beyond the per-member workspace mount the
/// dispatcher provisions itself (the per-agent volume at
/// <see cref="AgentWorkspaceContract.BuildMountPath"/>).
/// </param>
/// <param name="WorkingDirectory">
/// Optional working directory inside the container. When <c>null</c>, the
/// dispatcher uses the per-member workspace mount path.
/// </param>
/// <param name="Argv">
/// Optional argv vector the dispatcher should set as the container's
/// command. Each element becomes one argv entry — the dispatcher does not
/// shell-split the string. An empty list means "use the image's default
/// ENTRYPOINT/CMD" — the launch contract for images that already speak A2A
/// (e.g. <c>spring-voyages</c>) or images whose ENTRYPOINT is the Spring
/// agent-base bridge.
/// </param>
/// <param name="User">
/// Optional in-container user (uid[:gid] or username). When <c>null</c>,
/// the runtime uses the image's configured user.
/// </param>
/// <param name="A2APort">
/// TCP port the in-container A2A endpoint listens on. The dispatcher uses
/// this for the readiness probe (<c>GET /.well-known/agent.json</c>) and
/// for the A2A <c>message/send</c> call. Defaults to 8999, which is what
/// the Spring agent-base bridge and <c>spring-voyages</c> both listen on.
/// </param>
/// <param name="ResponseCapture">
/// How the dispatcher should capture the runtime's reasoning trace.
/// Defaults to <see cref="AgentResponseCapture.A2ATrace"/>; only that mode is
/// wired today, the other values exist so a future launcher can opt into a
/// different mechanism without bumping the launcher contract again.
/// </param>
public record AgentLaunchSpec(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<string>? ExtraVolumeMounts = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Argv = null,
    string? User = null,
    int A2APort = 8999,
    AgentResponseCapture ResponseCapture = AgentResponseCapture.A2ATrace);

/// <summary>
/// How the dispatcher captures the runtime's reasoning trace from the
/// container. Per
/// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
/// §3 these modes select <em>how the reasoning trace is captured</em>, not
/// how a response is delivered: every observable effect a runtime has on
/// the outside world flows through platform tool calls, and the terminal
/// text the modes here capture is diagnostic only. The captured bytes land
/// in <see cref="RuntimeOutcome.ReasoningTrace"/> and surface as a
/// <c>RuntimeReasoning</c> activity (capture-level controlled per
/// <see href="../../../docs/decisions/0054-one-mcp-server-one-execution-host.md">ADR-0054</see>).
/// </summary>
/// <remarks>
/// Only <see cref="A2ATrace"/> is wired today (and is the default). The other
/// values are reserved so a future launcher can opt into a different
/// capture mechanism without forcing a contract change. See ADR 0025
/// (introduced in PR 6 of the #1087 series).
/// </remarks>
public enum AgentResponseCapture
{
    /// <summary>
    /// Capture the reasoning trace from an A2A <c>message/send</c>
    /// roundtrip's task body on the in-container A2A endpoint (port
    /// <see cref="AgentLaunchSpec.A2APort"/>). This is the default and the
    /// only path implemented today.
    /// </summary>
    A2ATrace,

    /// <summary>
    /// Capture the reasoning trace by harvesting the container process's
    /// stdout after it exits. Reserved; not implemented.
    /// </summary>
    StdoutTrace,

    /// <summary>
    /// Capture the reasoning trace by reading a well-known file from the
    /// bind-mounted workspace after the container exits. Reserved; not
    /// implemented.
    /// </summary>
    FileTrace,
}
