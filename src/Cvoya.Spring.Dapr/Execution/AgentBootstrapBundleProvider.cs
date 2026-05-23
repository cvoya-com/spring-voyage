// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IAgentBootstrapBundleProvider"/>. Composes an agent's
/// bootstrap bundle (ADR-0055 §3) from:
/// <list type="bullet">
///   <item>the launcher's per-runtime contribution (system-prompt file +
///   MCP config — selected by <see cref="AgentExecutionConfig.Runtime"/>),</item>
///   <item>the agent-definition YAML + tenant-config JSON (D1 spec § 2.2.2),</item>
///   <item>the merged connector runtime-context contribution (per-binding
///   files under <c>connectors/&lt;slug&gt;/</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Determinism:</b> the bundle's <see cref="AgentBootstrapBundle.Version"/>
/// is the content-addressable sha256 of the canonical bundle bytes computed
/// by <see cref="AgentBootstrapBundleHasher"/>. Same inputs → same version,
/// across processes and across workers. The wallclock
/// <see cref="AgentBootstrapBundle.IssuedAt"/> is intentionally outside
/// the hash so 304 responses stay cheap.
/// </para>
/// </remarks>
public sealed class AgentBootstrapBundleProvider(
    IAgentDefinitionProvider agentDefinitionProvider,
    IAgentDefinitionSerializer agentDefinitionSerializer,
    IRuntimeCatalog runtimeCatalog,
    IEnumerable<IAgentRuntimeLauncher> launchers,
    IConnectorRuntimeContextResolver connectorContextResolver,
    IConnectorPromptContextResolver connectorPromptContextResolver,
    IPromptAssembler promptAssembler,
    IEnumerable<ISkillRegistry> skillRegistries,
    IServiceScopeFactory scopeFactory,
    IOptions<McpServerOptions> mcpServerOptions,
    ITenantContext tenantContext,
    TimeProvider timeProvider) : IAgentBootstrapBundleProvider
{
    /// <summary>
    /// Workspace-relative path of the agent-definition YAML mirroring the
    /// D1 spec <c>/spring/context/agent-definition.yaml</c> file.
    /// </summary>
    internal const string AgentDefinitionPath = "context/agent-definition.yaml";

    /// <summary>
    /// Workspace-relative path of the tenant-config JSON mirroring the
    /// D1 spec <c>/spring/context/tenant-config.json</c> file.
    /// </summary>
    internal const string TenantConfigPath = "context/tenant-config.json";

    private readonly IAgentDefinitionProvider _agentDefinitionProvider = agentDefinitionProvider
        ?? throw new ArgumentNullException(nameof(agentDefinitionProvider));
    private readonly IAgentDefinitionSerializer _agentDefinitionSerializer = agentDefinitionSerializer
        ?? throw new ArgumentNullException(nameof(agentDefinitionSerializer));
    private readonly IRuntimeCatalog _runtimeCatalog = runtimeCatalog
        ?? throw new ArgumentNullException(nameof(runtimeCatalog));
    private readonly Dictionary<string, IAgentRuntimeLauncher> _launchersByKind =
        launchers.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);
    private readonly IConnectorRuntimeContextResolver _connectorContextResolver = connectorContextResolver
        ?? throw new ArgumentNullException(nameof(connectorContextResolver));
    private readonly IConnectorPromptContextResolver _connectorPromptContextResolver = connectorPromptContextResolver
        ?? throw new ArgumentNullException(nameof(connectorPromptContextResolver));
    private readonly IPromptAssembler _promptAssembler = promptAssembler
        ?? throw new ArgumentNullException(nameof(promptAssembler));
    private readonly IReadOnlyList<ISkillRegistry> _skillRegistries = skillRegistries?.ToList()
        ?? throw new ArgumentNullException(nameof(skillRegistries));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory
        ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly McpServerOptions _mcpServerOptions = mcpServerOptions.Value;
    private readonly ITenantContext _tenantContext = tenantContext
        ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public async Task<AgentBootstrapBundle?> BuildAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var definition = await _agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken);
        if (definition is null)
        {
            // The agentId is HTTP-boundary input; the endpoint sanitises
            // it upstream. Surface the 404 to the caller without logging.
            return null;
        }

        var tenantId = _tenantContext.CurrentTenantId;

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentDefinitionPath] = _agentDefinitionSerializer.SerializeAgentDefinitionYaml(definition),
            [TenantConfigPath] = _agentDefinitionSerializer.SerializeTenantConfigJson(tenantId),
        };

        var platformFilePaths = new HashSet<string>(StringComparer.Ordinal);

        // Compose the per-agent system prompt (platform contract +
        // unit context + agent instructions + equipped skill bundles)
        // via the same assembler the dispatch path uses, then hand it
        // to the launcher contribution so each CLI runtime materialises
        // it into the file its CLI auto-discovers (CLAUDE.md / AGENTS.md
        // / GEMINI.md). Without this, the launcher contribution would
        // ship only Definition.Instructions and the agent would never
        // see the platform contract from Layer 1 — silent-dispatch
        // territory.
        var subjectAddress = new Address("agent", Guid.Parse(agentId));
        var connectorPromptFragments = await _connectorPromptContextResolver
            .ResolveAsync(subjectAddress, cancellationToken);
        var (unitBundles, agentBundles) = await EquippedBundleLoader.LoadAsync(
            _scopeFactory, agentId, cancellationToken);
        var skills = _skillRegistries
            .Select(r => new Skill(
                Name: r.Name,
                Description: $"Tools exposed by the {r.Name} connector.",
                Tools: r.GetToolDefinitions()))
            .ToList();
        var assemblyContext = new PromptAssemblyContext(
            Policies: null,
            Skills: skills,
            AgentInstructions: definition.Instructions,
            EffectiveMetadata: null,
            SkillBundles: unitBundles,
            AgentSkillBundles: agentBundles,
            PendingAmendments: null,
            ConnectorPromptFragments: connectorPromptFragments);
        var assembledSystemPrompt = await _promptAssembler.AssembleAsync(
            assemblyContext, cancellationToken);

        // Launcher contribution — the per-runtime system-prompt file and
        // MCP config (or nothing, for the A2A-native spring-voyage agent).
        var launcher = ResolveLauncher(definition, agentId);
        if (launcher is not null)
        {
            var ctx = new AgentBootstrapContributionContext(
                AgentId: agentId,
                Definition: definition,
                McpEndpoint: _mcpServerOptions.ContainerEndpoint,
                AssembledSystemPrompt: assembledSystemPrompt);
            var contribution = await launcher.ContributeBundleAsync(ctx, cancellationToken);
            foreach (var kvp in contribution.Files)
            {
                files[kvp.Key] = kvp.Value;
            }
            foreach (var p in contribution.PlatformFilePaths)
            {
                platformFilePaths.Add(p);
            }
        }

        // Connector contribution — per-binding files at
        // connectors/<slug>/<sub-path>. Env-var contributions ride the
        // dispatch path (A2AExecutionDispatcher); only file contributions
        // are folded into the bundle here.
        var connectorContext = await _connectorContextResolver.ResolveAsync(
            subjectAddress, cancellationToken);
        foreach (var kvp in connectorContext.ContextFiles)
        {
            files[kvp.Key] = kvp.Value;
        }

        // Files must be path-sorted for hash determinism — the hasher
        // sorts internally but emitting sorted output keeps the bundle
        // wire shape stable for the sidecar's debug-level logging.
        var orderedFiles = files
            .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new AgentBootstrapFile(
                kvp.Key,
                AgentBootstrapBundleHasher.ComputeFileHash(kvp.Value),
                kvp.Value))
            .ToList();

        var platformFileHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var platformPath in platformFilePaths)
        {
            var match = orderedFiles.FirstOrDefault(f => f.Path == platformPath);
            if (match is not null)
            {
                platformFileHashes[platformPath] = match.Sha256;
            }
        }

        var version = AgentBootstrapBundleHasher.Compute(orderedFiles, platformFileHashes);

        return new AgentBootstrapBundle(
            Version: version,
            IssuedAt: _timeProvider.GetUtcNow(),
            Files: orderedFiles,
            PlatformFileHashes: platformFileHashes);
    }

    private IAgentRuntimeLauncher? ResolveLauncher(AgentDefinition definition, string agentId)
    {
        var runtimeId = definition.Execution?.Runtime;
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return null;
        }
        var runtime = _runtimeCatalog.GetAgentRuntime(runtimeId);
        if (runtime is null)
        {
            return null;
        }
        return _launchersByKind.TryGetValue(runtime.Launcher, out var launcher)
            ? launcher
            : null;
    }
}
