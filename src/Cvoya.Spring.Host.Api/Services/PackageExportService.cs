// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Default <see cref="IPackageExportService"/> implementation.
/// <para>
/// <b>Runtime/DB config is the single source of truth (#3090; supersedes
/// ADR-0035 decision 12).</b> Export <i>reconstructs</i> a re-installable
/// package from the live relational stores — definitions, live-config /
/// execution, memberships, expertise, policies, and connector bindings — so
/// every post-deploy edit (rename, instructions, model swap, hosting change,
/// membership/role, expertise, policy, connector reconfig) is reflected. The
/// captured <c>original_manifest_yaml</c> is no longer the export source; it
/// remains only as install-replay provenance for the retry/abort path.
/// </para>
/// <para>
/// The reconstructed package is returned as a zip archive whose entries are
/// rooted at <c>&lt;package-name&gt;/</c>, matching the ADR-0043 recursive
/// folder layout, so a downstream unzip produces a tree the installer
/// re-ingests. Connector <c>config</c> and any bound secret are never
/// exported — bindings render as <c>requires:</c> placeholders.
/// </para>
/// </summary>
public sealed class PackageExportService : IPackageExportService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDirectoryService _directoryService;
    private readonly ILogger<PackageExportService> _logger;

    /// <summary>
    /// Initialises a new <see cref="PackageExportService"/>.
    /// </summary>
    public PackageExportService(
        IServiceScopeFactory scopeFactory,
        IDirectoryService directoryService,
        ILogger<PackageExportService> logger)
    {
        _scopeFactory = scopeFactory;
        _directoryService = directoryService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PackageExportResult?> ExportByUnitNameAsync(
        string unitName,
        bool withValues,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitName);

        // Verify the unit (or agent) exists in the current tenant's directory.
        // Manifests carry display names (not Guids), so look up by display
        // name through ListAllAsync rather than Address.For (Guid-only post-#1629).
        var allEntries = await _directoryService.ListAllAsync(cancellationToken);
        var entry = allEntries.FirstOrDefault(
            e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                 && string.Equals(e.DisplayName, unitName, StringComparison.Ordinal));

        if (entry is null)
        {
            // Try agent scheme — a package may install an agent rather than a unit.
            entry = allEntries.FirstOrDefault(
                e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(e.DisplayName, unitName, StringComparison.Ordinal));
        }

        if (entry is null)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Resolve the package name from the install row (when the artefact was
        // package-installed); fall back to the artefact's display name so an
        // operator-created (non-package) unit can still be exported.
        var packageName = await ResolvePackageNameAsync(db, entry, cancellationToken);

        var reconstructor = BuildReconstructor(scope, db);
        var reconstructed = string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
            ? await reconstructor.ReconstructFromUnitAsync(entry.ActorId, packageName, cancellationToken)
            : await reconstructor.ReconstructFromAgentAsync(entry.ActorId, packageName, cancellationToken);

        return reconstructed is null ? null : BuildResult(reconstructed);
    }

    /// <inheritdoc />
    public async Task<PackageExportResult?> ExportByInstallIdAsync(
        Guid installId,
        bool withValues,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // The EF query filter on PackageInstallEntity scopes to CurrentTenantId.
        var rows = await db.PackageInstalls
            .Where(r => r.InstallId == installId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return null;
        }

        if (rows.Count > 1)
        {
            // Multi-package install: v0.1 reconstructs the first package only.
            // Multi-target tarball export is deferred — see #1579.
            _logger.LogWarning(
                "ExportByInstallIdAsync: install '{InstallId}' contains {Count} packages; " +
                "reconstructing first package '{PackageName}' only. " +
                "Multi-target tarball export is not yet supported.",
                installId, rows.Count, rows[0].PackageName);
        }

        var row = rows[0];

        // Find the package's top-level unit (or agent) by install id and
        // reconstruct from runtime state.
        var unit = await db.UnitDefinitions
            .Where(u => u.InstallId == installId && u.DeletedAt == null)
            .OrderBy(u => u.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var reconstructor = BuildReconstructor(scope, db);

        if (unit is not null)
        {
            var reconstructed = await reconstructor.ReconstructFromUnitAsync(
                unit.Id, row.PackageName, cancellationToken);
            return reconstructed is null ? null : BuildResult(reconstructed);
        }

        // AgentPackage shape: no unit staging row. Resolve the single agent the
        // install produced through the directory and reconstruct from it.
        var agentEntry = await ResolveInstalledAgentAsync(db, row.PackageName, cancellationToken);
        if (agentEntry is null)
        {
            _logger.LogDebug(
                "ExportByInstallIdAsync: install '{InstallId}' has no reconstructable unit or agent.",
                installId);
            return null;
        }

        var agentReconstructed = await reconstructor.ReconstructFromAgentAsync(
            agentEntry.Value, row.PackageName, cancellationToken);
        return agentReconstructed is null ? null : BuildResult(agentReconstructed);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private PackageReconstructor BuildReconstructor(AsyncServiceScope scope, SpringDbContext db)
    {
        // Build the TypeId → Slug map from the registered connector types in a
        // fresh resolve (cycle-safe — never constructor-injected downstream of
        // the binding store). Used to render unit connector bindings as
        // `requires:` entries by slug.
        var connectorSlugs = scope.ServiceProvider
            .GetServices<IConnectorType>()
            .GroupBy(c => c.TypeId)
            .ToDictionary(g => g.Key, g => g.First().Slug);

        return new PackageReconstructor(
            db,
            scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitPolicyRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingStore>(),
            scope.ServiceProvider.GetRequiredService<IAgentDefinitionProvider>(),
            connectorSlugs);
    }

    private static async Task<string> ResolvePackageNameAsync(
        SpringDbContext db,
        DirectoryEntry entry,
        CancellationToken cancellationToken)
    {
        if (string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            var unitRow = await db.UnitDefinitions
                .AsNoTracking()
                .Where(u => u.Id == entry.ActorId && u.DeletedAt == null)
                .FirstOrDefaultAsync(cancellationToken);
            if (unitRow?.InstallId is { } installId)
            {
                var install = await db.PackageInstalls
                    .AsNoTracking()
                    .Where(r => r.InstallId == installId)
                    .FirstOrDefaultAsync(cancellationToken);
                if (install is not null && !string.IsNullOrWhiteSpace(install.PackageName))
                {
                    return install.PackageName;
                }
            }
        }

        // No install linkage (operator-created artefact) — derive a stable,
        // re-installable package name from the artefact's display name.
        return DerivePackageName(entry.DisplayName);
    }

    private static async Task<Guid?> ResolveInstalledAgentAsync(
        SpringDbContext db,
        string packageName,
        CancellationToken cancellationToken)
    {
        // AgentPackage installs have no unit staging row; the agent name
        // matches the package's single top-level agent. Best-effort lookup by
        // the package name as a display-name candidate.
        var agent = await db.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.DisplayName == packageName && a.DeletedAt == null)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        return agent?.Id;
    }

    private static string DerivePackageName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "exported-package";
        }
        var sb = new StringBuilder(displayName.Length);
        foreach (var ch in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.')
            {
                sb.Append('-');
            }
        }
        var result = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "exported-package" : result;
    }

    /// <summary>
    /// Builds a <see cref="PackageExportResult"/> zip archive from a
    /// reconstructed package. Every entry is rooted at
    /// <c>&lt;packageName&gt;/…</c> so unzipping produces a single top-level
    /// folder named for the package (ADR-0043 §1).
    /// </summary>
    public static PackageExportResult BuildResult(ReconstructedPackage reconstructed)
    {
        var bytes = ZipReconstructed(reconstructed);
        return new PackageExportResult(
            PackageName: reconstructed.PackageName,
            Content: bytes,
            ContentType: "application/zip",
            FileName: $"{reconstructed.PackageName}.zip");
    }

    private static byte[] ZipReconstructed(ReconstructedPackage reconstructed)
    {
        var rootPrefix = reconstructed.PackageName + "/";
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Root package.yaml.
            WriteEntry(zip, rootPrefix + "package.yaml", SerializePackage(reconstructed.Package));

            // Artefact documents under their reconstructed relative paths.
            foreach (var document in reconstructed.Documents)
            {
                WriteEntry(zip, rootPrefix + document.RelativePath, document.Yaml);
            }
        }
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string SerializePackage(PackageManifest package)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .DisableAliases()
            .Build();
        var body = serializer.Serialize(package);
        return body.EndsWith('\n') ? body : body + "\n";
    }
}
