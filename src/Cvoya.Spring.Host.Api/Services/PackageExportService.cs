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

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPackageExportService"/> implementation.
/// ADR-0043 §1/§2: a package is a folder rooted at <c>package.yaml</c>
/// with nested artefacts under <c>units/</c> / <c>agents/</c> / etc.
/// The exporter packages that folder tree into a zip archive.
/// <para>
/// For installs whose <see cref="Dapr.Data.Entities.PackageInstallEntity.PackageRoot"/>
/// points at an on-disk catalog directory the exporter zips that directory
/// verbatim (preserving authoring intent — comments, key order, companion
/// markdown). For uploads where no <c>PackageRoot</c> was preserved the
/// exporter falls back to a one-file zip carrying the persisted
/// <c>OriginalManifestYaml</c> at <c>&lt;pkg&gt;/package.yaml</c>.
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
            installRow.PackageRoot);
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
            return BuildResult(row.PackageName, row.OriginalManifestYaml, row.PackageRoot);
        }

        // Multi-package install: v0.1 returns the first package's export.
        // Multi-target tarball export is deferred — see #1579.
        var first = rows[0];
        _logger.LogWarning(
            "ExportByInstallIdAsync: install '{InstallId}' contains {Count} packages; " +
            "returning first package '{PackageName}' only. " +
            "Multi-target tarball export is not yet supported.",
            installId, rows.Count, first.PackageName);
        return BuildResult(first.PackageName, first.OriginalManifestYaml, first.PackageRoot);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="PackageExportResult"/> as a zip archive. When a
    /// <paramref name="packageRoot"/> is supplied and the directory still
    /// exists on disk the exporter zips the recursive folder tree
    /// verbatim. Otherwise the result is a one-file zip with the persisted
    /// root <c>package.yaml</c>. Exposed publicly so the round-trip layout
    /// can be unit-tested directly.
    /// </summary>
    public static PackageExportResult BuildResult(
        string packageName,
        string originalYaml,
        string? packageRoot)
    {
        var bytes = !string.IsNullOrEmpty(packageRoot) && Directory.Exists(packageRoot)
            ? ZipDirectory(packageRoot, packageName)
            : ZipFallback(packageName, originalYaml);

        return new PackageExportResult(
            PackageName: packageName,
            Content: bytes,
            ContentType: "application/zip",
            FileName: $"{packageName}.zip");
    }

    /// <summary>
    /// Zips the on-disk package directory tree. Every entry is rooted at
    /// <c>&lt;packageName&gt;/…</c> inside the archive so unzipping
    /// produces a single top-level folder named for the package
    /// (matching ADR-0043 §1's "every standalone artefact is a folder
    /// rooted at package.yaml").
    /// </summary>
    private static byte[] ZipDirectory(string packageRoot, string packageName)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var rootPrefix = packageName + "/";
            foreach (var filePath in EnumerateContentFiles(packageRoot))
            {
                var rel = Path.GetRelativePath(packageRoot, filePath);
                // Normalise to forward-slash for cross-platform zip entries.
                var entryName = rootPrefix + rel.Replace(Path.DirectorySeparatorChar, '/');
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var fileStream = File.OpenRead(filePath);
                fileStream.CopyTo(entryStream);
            }
        }
        return memory.ToArray();
    }

    /// <summary>
    /// One-file zip carrying just the persisted root <c>package.yaml</c>.
    /// Used when the install row has no <c>PackageRoot</c> (file-upload
    /// path) or when the directory has been moved / deleted since the
    /// install.
    /// </summary>
    private static byte[] ZipFallback(string packageName, string originalYaml)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"{packageName}/package.yaml", CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(originalYaml);
            stream.Write(bytes, 0, bytes.Length);
        }
        return memory.ToArray();
    }

    /// <summary>
    /// Enumerates every file under <paramref name="packageRoot"/> that
    /// should land in the exported archive. Conventional content lives
    /// at the root (<c>package.yaml</c>, <c>README.md</c>) and under the
    /// conventional subdirectories. Hidden directories
    /// (<c>.git/</c>, <c>node_modules/</c>, etc.) are skipped.
    /// </summary>
    private static IEnumerable<string> EnumerateContentFiles(string packageRoot)
    {
        var stack = new Stack<string>();
        stack.Push(packageRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(child);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                {
                    continue;
                }
                if (string.Equals(name, "node_modules", StringComparison.Ordinal)
                    || string.Equals(name, "bin", StringComparison.Ordinal)
                    || string.Equals(name, "obj", StringComparison.Ordinal))
                {
                    continue;
                }
                stack.Push(child);
            }
        }
    }
}
