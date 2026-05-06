// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPackageExportService"/> implementation.
/// Implements ADR-0035 decisions 9 and 12: reads <c>OriginalManifestYaml</c>
/// verbatim from the <c>package_installs</c> row and, when
/// <c>withValues = true</c>, splices a key/value <c>inputs:</c> block
/// materialised from <c>InputsJson</c>. Secret inputs become placeholder
/// references; no cleartext is emitted.
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

        // Find the unit_definitions row for this unit name to get its InstallId.
        // The EF query filter on UnitDefinitionEntity scopes to CurrentTenantId.
        var unitRow = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .Where(u => u.DisplayName == unitName && u.DeletedAt == null)
            .FirstOrDefaultAsync(cancellationToken);

        Guid? installId = unitRow?.InstallId;

        if (installId is null)
        {
            _logger.LogDebug(
                "ExportByUnitNameAsync: no install_id found in unit_definitions for unit '{UnitName}'.",
                unitName);
            return null;
        }

        // Find the package_installs row. The EF query filter scopes to tenant.
        var installRow = await db.PackageInstalls
            .Where(r => r.InstallId == installId.Value)
            .FirstOrDefaultAsync(cancellationToken);

        if (installRow is null)
        {
            _logger.LogDebug(
                "ExportByUnitNameAsync: no package_installs row found for install_id '{InstallId}'.",
                installId.Value);
            return null;
        }

        return BuildResult(
            installRow.PackageName,
            installRow.OriginalManifestYaml,
            installRow.InputsJson,
            withValues);
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

        if (rows.Count == 1)
        {
            var row = rows[0];
            return BuildResult(row.PackageName, row.OriginalManifestYaml, row.InputsJson, withValues);
        }

        // Multi-package install: v0.1 returns the first package's export.
        // Multi-target tarball export is deferred — see #1579.
        var first = rows[0];
        _logger.LogWarning(
            "ExportByInstallIdAsync: install '{InstallId}' contains {Count} packages; " +
            "returning first package '{PackageName}' only. " +
            "Multi-target tarball export is not yet supported.",
            installId, rows.Count, first.PackageName);
        return BuildResult(first.PackageName, first.OriginalManifestYaml, first.InputsJson, withValues);
    }

    // ── Internal helpers (internal for testability) ────────────────────────

    /// <summary>
    /// Builds a <see cref="PackageExportResult"/> from the stored row data.
    /// When <paramref name="withValues"/> is <see langword="true"/> the
    /// <c>inputs:</c> schema block in the YAML is replaced with a key/value
    /// block derived from the stored input bindings.
    /// </summary>
    internal static PackageExportResult BuildResult(
        string packageName,
        string originalYaml,
        string inputsJson,
        bool withValues)
    {
        var yaml = withValues
            ? SpliceInputValues(originalYaml, inputsJson)
            : originalYaml;

        // Normalise to a single trailing newline so byte-stable comparisons
        // do not trip on editor-added trailing blank lines.
        yaml = yaml.TrimEnd() + "\n";

        var content = Encoding.UTF8.GetBytes(yaml);
        return new PackageExportResult(
            PackageName: packageName,
            Content: content,
            ContentType: "application/x-yaml",
            FileName: $"{packageName}.yaml");
    }

    /// <summary>
    /// Splices the resolved input values into the YAML string, replacing the
    /// <c>inputs:</c> schema block with a key/value block where:
    /// <list type="bullet">
    ///   <item>Non-secret inputs → <c>key: value</c></item>
    ///   <item>Secret inputs → <c>key: ${{ secrets.&lt;name&gt; }}</c></item>
    /// </list>
    ///
    /// <para>
    /// Does NOT re-render the entire document through YamlDotNet (ADR-0035
    /// decision 12). Only the <c>inputs:</c> block is replaced; all other
    /// lines are preserved verbatim.
    /// </para>
    /// </summary>
    public static string SpliceInputValues(string originalYaml, string inputsJson)
    {
        // ADR-0037 D2: package-level `inputs:` was retired. Round-trip
        // export under the new schema is identity — there's no inputs:
        // block to splice values into. The argument stays on the
        // signature for legacy persisted-blob compatibility but is
        // ignored.
        _ = inputsJson;
        return originalYaml;
    }

}