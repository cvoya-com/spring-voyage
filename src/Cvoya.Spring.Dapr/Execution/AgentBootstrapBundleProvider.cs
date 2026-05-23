// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IAgentBootstrapBundleProvider"/>. Composes the static
/// subset of an agent's bootstrap bundle (ADR-0055 §3) from the agent
/// definition and the current tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wave 1 scope:</b> the bundle contains the platform-authoritative
/// <c>CLAUDE.md</c> (sourced from <see cref="AgentDefinition.Instructions"/>),
/// plus the <c>/spring/context/</c> files <c>agent-definition.yaml</c> and
/// <c>tenant-config.json</c>. Launcher-specific files such as
/// <c>.mcp.json</c> enter the bundle in Wave 3 when launchers stop emitting
/// <see cref="AgentLaunchSpec.WorkspaceFiles"/> entirely and contribute
/// their static files through this provider instead.
/// </para>
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
    ITenantContext tenantContext,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IAgentBootstrapBundleProvider
{
    /// <summary>
    /// Workspace-relative path of the platform-authoritative system prompt
    /// file. The sidecar pins this on every turn via the integrity check
    /// (ADR-0055 §6).
    /// </summary>
    internal const string ClaudeMdPath = "CLAUDE.md";

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
    private readonly ITenantContext _tenantContext = tenantContext
        ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentBootstrapBundleProvider>();

    /// <inheritdoc />
    public async Task<AgentBootstrapBundle?> BuildAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var definition = await _agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken);
        if (definition is null)
        {
            _logger.LogDebug(
                "Bootstrap bundle requested for unknown agent {AgentId}; returning null",
                agentId);
            return null;
        }

        var tenantId = _tenantContext.CurrentTenantId;

        var claudeMd = definition.Instructions ?? string.Empty;
        var agentDefinitionYaml = _agentDefinitionSerializer.SerializeAgentDefinitionYaml(definition);
        var tenantConfigJson = _agentDefinitionSerializer.SerializeTenantConfigJson(tenantId);

        // Files must be emitted in path-sorted order for hash determinism —
        // see AgentBootstrapBundleHasher. CLAUDE.md sorts before context/*.
        var files = new List<AgentBootstrapFile>
        {
            BuildFile(ClaudeMdPath, claudeMd),
            BuildFile(AgentDefinitionPath, agentDefinitionYaml),
            BuildFile(TenantConfigPath, tenantConfigJson),
        };
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        // Wave 1: only CLAUDE.md is platform-authoritative. .mcp.json joins
        // the pinned set in Wave 3 when the launcher contribution lands.
        var platformFileHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ClaudeMdPath] = files.First(f => f.Path == ClaudeMdPath).Sha256,
        };

        var version = AgentBootstrapBundleHasher.Compute(files, platformFileHashes);

        return new AgentBootstrapBundle(
            Version: version,
            IssuedAt: _timeProvider.GetUtcNow(),
            Files: files,
            PlatformFileHashes: platformFileHashes);
    }

    private static AgentBootstrapFile BuildFile(string path, string content)
        => new(path, AgentBootstrapBundleHasher.ComputeFileHash(content), content);
}
