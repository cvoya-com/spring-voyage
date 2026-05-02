// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IPackageInstallService"/> implementation.
/// Implements ADR-0035 decisions 10, 11, 12, and 14:
/// Phase 1 — single EF transaction: validate, topo-sort, collision pre-flight,
/// write staging rows. Phase 2 — post-commit: activate actors in dep order.
/// </summary>
public class PackageInstallService : IPackageInstallService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDirectoryService _directoryService;
    private readonly IPackageArtefactActivator _activator;
    private readonly IPackageCatalogProvider? _catalogProvider;
    private readonly ILogger<PackageInstallService> _logger;

    /// <summary>
    /// Initialises a new <see cref="PackageInstallService"/>.
    /// </summary>
    public PackageInstallService(
        IServiceScopeFactory scopeFactory,
        IDirectoryService directoryService,
        IPackageArtefactActivator activator,
        ILogger<PackageInstallService> logger,
        IPackageCatalogProvider? catalogProvider = null)
    {
        _scopeFactory = scopeFactory;
        _directoryService = directoryService;
        _activator = activator;
        _logger = logger;
        _catalogProvider = catalogProvider;
    }

    /// <inheritdoc />
    public async Task<InstallResult> InstallAsync(
        IReadOnlyList<InstallTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one install target is required.", nameof(targets));
        }

        var installId = Guid.NewGuid();

        // ── Phase 1 ────────────────────────────────────────────────────────
        // Parse + resolve all packages, validate dep-graph closure, collision
        // pre-flight, write staging rows — all in a single EF transaction.
        // Any failure → rollback → re-throw (zero rows survive).

        List<(InstallTarget Target, ResolvedPackage Package)> resolvedTargets;
        try
        {
            resolvedTargets = await ResolveAllTargetsAsync(targets, cancellationToken);
        }
        catch (PackageDepGraphException)
        {
            throw;
        }
        catch (PackageNameCollisionException)
        {
            throw;
        }

        // Topological sort of packages by cross-package reference order.
        // dep-provider packages come first so Phase 2 activates dependents
        // after their dependencies.
        var sorted = TopologicalSort(resolvedTargets);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Name collision pre-flight (ADR-0035 decision 10): collect every
            // artefact name across all packages, check each against the directory.
            await PreflightNameCollisionsAsync(sorted, cancellationToken);

            // Write all staging rows.
            var now = DateTimeOffset.UtcNow;
            foreach (var (target, pkg) in sorted)
            {
                var installRow = new PackageInstallEntity
                {
                    Id = Guid.NewGuid(),
                    InstallId = installId,
                    PackageName = pkg.Name,
                    Status = PackageInstallStatus.Staging,
                    OriginalManifestYaml = target.OriginalYaml,
                    InputsJson = JsonSerializer.Serialize(pkg.InputValues),
                    PackageRoot = string.IsNullOrEmpty(target.PackageRoot) ? null : target.PackageRoot,
                    StartedAt = now,
                };
                db.PackageInstalls.Add(installRow);

                // Write unit_definitions staging rows.
                foreach (var unit in pkg.Units.Where(a => !a.IsCrossPackage))
                {
                    var existing = await db.UnitDefinitions
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(u =>
                            u.UnitId == unit.Name && u.DeletedAt == null,
                            cancellationToken);
                    if (existing is null)
                    {
                        var entity = new UnitDefinitionEntity
                        {
                            Id = Guid.NewGuid(),
                            UnitId = unit.Name,
                            Name = unit.Name,
                            Description = string.Empty,
                            InstallState = PackageInstallState.Staging,
                            InstallId = installId,
                            CreatedAt = now,
                            UpdatedAt = now,
                        };
                        db.UnitDefinitions.Add(entity);
                    }
                    else
                    {
                        existing.InstallState = PackageInstallState.Staging;
                        existing.InstallId = installId;
                    }
                }

                // Write agent-level entries as unit_definitions with agent scheme.
                // For AgentPackage, agents are registered in agent_definitions
                // (via directory service in Phase 2). The staging row here tracks
                // the install lifecycle only — Phase 2 handles the actor activation.
                // Agent staging rows in unit_definitions for tracking:
                // (The actual agent_definitions row is created in Phase 2 via
                //  directory service, consistent with existing agent-creation path.)
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        // ── Phase 2 ────────────────────────────────────────────────────────
        // Activate actors in dep order. Failures leave staging rows visible.
        var packageResults = new List<PackageInstallResult>();
        foreach (var (target, pkg) in sorted)
        {
            var (outcome, error) = await ActivatePackageAsync(pkg, installId, cancellationToken);
            packageResults.Add(new PackageInstallResult(pkg.Name, outcome, error));

            // Update the package_installs row for this package.
            await UpdatePackageInstallRowAsync(installId, pkg.Name,
                outcome == PackageInstallOutcome.Active
                    ? PackageInstallStatus.Active
                    : PackageInstallStatus.Failed,
                error,
                cancellationToken);
        }

        return new InstallResult(installId, packageResults);
    }

    /// <inheritdoc />
    public async Task<InstallStatus?> GetStatusAsync(
        Guid installId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.PackageInstalls
            .Where(r => r.InstallId == installId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return null;
        }

        var packages = rows.Select(r => new PackageInstallResult(
            r.PackageName,
            r.Status switch
            {
                PackageInstallStatus.Active => PackageInstallOutcome.Active,
                PackageInstallStatus.Failed => PackageInstallOutcome.Failed,
                _ => PackageInstallOutcome.Staging,
            },
            r.ErrorMessage)).ToList();

        return new InstallStatus(installId, packages);
    }

    /// <inheritdoc />
    public async Task<InstallResult> RetryAsync(
        Guid installId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.PackageInstalls
            .Where(r => r.InstallId == installId)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Install '{installId}' not found in the current tenant.");
        }

        // Only retry packages that are not yet active.
        var toRetry = rows.Where(r => r.Status != PackageInstallStatus.Active).ToList();
        var packageResults = new List<PackageInstallResult>();

        // Re-parse each package from its stored YAML + inputs to get the resolved artefacts.
        foreach (var row in rows)
        {
            if (row.Status == PackageInstallStatus.Active)
            {
                packageResults.Add(new PackageInstallResult(
                    row.PackageName, PackageInstallOutcome.Active, null));
                continue;
            }

            // Re-resolve from stored YAML.
            var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(row.InputsJson)
                ?? new Dictionary<string, string>();

            ResolvedPackage pkg;
            try
            {
                pkg = await PackageManifestParser.ParseAndResolveAsync(
                    row.OriginalManifestYaml,
                    packageRoot: row.PackageRoot ?? string.Empty,
                    inputValues: inputs,
                    catalogProvider: _catalogProvider,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Retry: failed to re-parse package '{Package}' for install '{InstallId}'.",
                    row.PackageName, installId);
                packageResults.Add(new PackageInstallResult(
                    row.PackageName, PackageInstallOutcome.Failed,
                    $"Re-parse failed: {ex.Message}"));
                continue;
            }

            var (outcome, error) = await ActivatePackageAsync(pkg, installId, cancellationToken);
            packageResults.Add(new PackageInstallResult(row.PackageName, outcome, error));

            await UpdatePackageInstallRowAsync(installId, row.PackageName,
                outcome == PackageInstallOutcome.Active
                    ? PackageInstallStatus.Active
                    : PackageInstallStatus.Failed,
                error, cancellationToken);
        }

        return new InstallResult(installId, packageResults);
    }

    /// <inheritdoc />
    public async Task AbortAsync(
        Guid installId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Delete unit_definitions staging rows for this install.
            var unitRows = await db.UnitDefinitions
                .IgnoreQueryFilters()
                .Where(u => u.InstallId == installId)
                .ToListAsync(cancellationToken);
            db.UnitDefinitions.RemoveRange(unitRows);

            // Delete connector_definitions staging rows for this install.
            var connRows = await db.ConnectorDefinitions
                .IgnoreQueryFilters()
                .Where(c => c.InstallId == installId)
                .ToListAsync(cancellationToken);
            db.ConnectorDefinitions.RemoveRange(connRows);

            // Delete tenant_skill_bundle_bindings staging rows for this install.
            var bundleRows = await db.TenantSkillBundleBindings
                .IgnoreQueryFilters()
                .Where(b => b.InstallId == installId)
                .ToListAsync(cancellationToken);
            db.TenantSkillBundleBindings.RemoveRange(bundleRows);

            // Delete package_installs rows.
            var installRows = await db.PackageInstalls
                .Where(r => r.InstallId == installId)
                .ToListAsync(cancellationToken);
            db.PackageInstalls.RemoveRange(installRows);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    // ── Phase 1 helpers ────────────────────────────────────────────────────

    private async Task<List<(InstallTarget Target, ResolvedPackage Package)>> ResolveAllTargetsAsync(
        IReadOnlyList<InstallTarget> targets,
        CancellationToken cancellationToken)
    {
        var result = new List<(InstallTarget, ResolvedPackage)>(targets.Count);

        // Build an in-flight overlay catalog so each package can resolve
        // cross-package references to other packages in this batch before
        // the batch has been committed to the database (ADR-0035 decision 14).
        var inFlightPackages = new Dictionary<string, (InstallTarget Target, string PackageRoot)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            inFlightPackages[t.PackageName] = (t, t.PackageRoot ?? string.Empty);
        }

        var overlayCatalog = new InFlightBatchCatalogProvider(inFlightPackages, _catalogProvider);

        foreach (var target in targets)
        {
            var pkg = await PackageManifestParser.ParseAndResolveAsync(
                target.OriginalYaml,
                packageRoot: target.PackageRoot ?? string.Empty,
                inputValues: target.Inputs,
                catalogProvider: overlayCatalog,
                cancellationToken: cancellationToken);

            result.Add((target, pkg));
        }

        // Validate dep-graph closure: every cross-package reference must resolve
        // to a package in this batch or to an already-installed package.
        await ValidateDepGraphClosureAsync(result, cancellationToken);

        return result;
    }

    private async Task ValidateDepGraphClosureAsync(
        List<(InstallTarget Target, ResolvedPackage Package)> resolved,
        CancellationToken cancellationToken)
    {
        var batchPackageNames = new HashSet<string>(
            resolved.Select(r => r.Package.Name),
            StringComparer.OrdinalIgnoreCase);

        // Gather all cross-package artefact names that are referenced.
        var missingRefs = new List<string>();
        foreach (var (_, pkg) in resolved)
        {
            foreach (var artefacts in new[] { pkg.Units, pkg.Agents, pkg.Skills, pkg.Workflows })
            {
                foreach (var a in artefacts.Where(a => a.IsCrossPackage))
                {
                    var sourcePackage = a.SourcePackage!;
                    if (batchPackageNames.Contains(sourcePackage))
                    {
                        continue;  // satisfied by another package in the batch
                    }

                    // Check whether this package is already installed in the tenant.
                    var installedExists = _catalogProvider is not null
                        && await _catalogProvider.PackageExistsAsync(sourcePackage, cancellationToken);

                    if (!installedExists)
                    {
                        missingRefs.Add(
                            $"package {pkg.Name} references {sourcePackage}/{a.Name}, " +
                            $"which is not in the install batch and not installed in this tenant");
                    }
                }
            }
        }

        if (missingRefs.Count > 0)
        {
            throw new PackageDepGraphException(missingRefs);
        }
    }

    private static List<(InstallTarget Target, ResolvedPackage Package)> TopologicalSort(
        List<(InstallTarget Target, ResolvedPackage Package)> items)
    {
        // Build dependency map: packageName → set of packages it depends on.
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, (InstallTarget Target, ResolvedPackage Package)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            byName[item.Package.Name] = item;
            var crossRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var artefacts in new[] { item.Package.Units, item.Package.Agents,
                item.Package.Skills, item.Package.Workflows })
            {
                foreach (var a in artefacts.Where(a => a.IsCrossPackage))
                {
                    crossRefs.Add(a.SourcePackage!);
                }
            }
            // Keep only references to other packages in this batch.
            crossRefs.IntersectWith(byName.Keys);
            deps[item.Package.Name] = crossRefs;
        }

        // Kahn's algorithm.
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in byName.Keys) inDegree[name] = 0;
        foreach (var (name, depSet) in deps)
        {
            foreach (var dep in depSet)
            {
                if (inDegree.ContainsKey(dep))
                {
                    inDegree[dep]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<(InstallTarget, ResolvedPackage)>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(byName[node]);

            foreach (var (depName, depSet) in deps)
            {
                if (depSet.Contains(node))
                {
                    inDegree[depName]--;
                    if (inDegree[depName] == 0)
                    {
                        queue.Enqueue(depName);
                    }
                }
            }
        }

        // If we haven't placed everything, there's a cycle (should be caught by parser).
        if (sorted.Count != items.Count)
        {
            // Return original order — cycle detection is the parser's job.
            return items;
        }

        return sorted;
    }

    private async Task PreflightNameCollisionsAsync(
        List<(InstallTarget Target, ResolvedPackage Package)> sorted,
        CancellationToken cancellationToken)
    {
        var collisions = new List<string>();

        foreach (var (_, pkg) in sorted)
        {
            foreach (var artefacts in new[] { pkg.Units, pkg.Agents })
            {
                foreach (var a in artefacts.Where(a => !a.IsCrossPackage))
                {
                    var scheme = a.Kind == ArtefactKind.Unit ? "unit" : "agent";
                    var address = new Address(scheme, a.Name);
                    var existing = await _directoryService.ResolveAsync(address, cancellationToken);
                    if (existing is not null)
                    {
                        collisions.Add(a.Name);
                    }
                }
            }
        }

        if (collisions.Count > 0)
        {
            throw new PackageNameCollisionException(collisions);
        }
    }

    // ── Phase 2 helpers ────────────────────────────────────────────────────

    private async Task<(PackageInstallOutcome Outcome, string? Error)> ActivatePackageAsync(
        ResolvedPackage pkg,
        Guid installId,
        CancellationToken cancellationToken)
    {
        string? firstError = null;
        var allSucceeded = true;

        // Activate units first (parents before sub-units where possible).
        // Within a package, the parser has already validated cycles; process
        // in declaration order which respects the sub-unit nesting.
        foreach (var artefact in pkg.Units.Concat(pkg.Agents)
            .Where(a => !a.IsCrossPackage))
        {
            try
            {
                await _activator.ActivateAsync(pkg.Name, artefact, installId, cancellationToken);
                await FlipArtefactStateToActiveAsync(artefact, installId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                _logger.LogWarning(ex,
                    "Phase 2: activation failed for {Kind} '{Name}' in package '{Package}' (install {InstallId}).",
                    artefact.Kind, artefact.Name, pkg.Name, installId);
                await FlipArtefactStateToFailedAsync(artefact, installId, msg, cancellationToken);
                firstError ??= msg;
                allSucceeded = false;
                // Continue — every artefact gets its best shot.
            }
        }

        return allSucceeded
            ? (PackageInstallOutcome.Active, null)
            : (PackageInstallOutcome.Failed, firstError);
    }

    private async Task FlipArtefactStateToActiveAsync(
        ResolvedArtefact artefact,
        Guid installId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (artefact.Kind == ArtefactKind.Unit)
        {
            var row = await db.UnitDefinitions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u =>
                    u.InstallId == installId && u.UnitId == artefact.Name,
                    cancellationToken);
            if (row is not null)
            {
                row.InstallState = PackageInstallState.Active;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        // Agents are activated via the directory service in Phase 2 and don't
        // have a separate staging row in unit_definitions written in Phase 1.
    }

    private async Task FlipArtefactStateToFailedAsync(
        ResolvedArtefact artefact,
        Guid installId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            if (artefact.Kind == ArtefactKind.Unit)
            {
                var row = await db.UnitDefinitions
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(u =>
                        u.InstallId == installId && u.UnitId == artefact.Name,
                        cancellationToken);
                if (row is not null)
                {
                    row.InstallState = PackageInstallState.Failed;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Phase 2: failed to flip state to Failed for {Kind} '{Name}' (install {InstallId}).",
                artefact.Kind, artefact.Name, installId);
        }
    }

    private async Task UpdatePackageInstallRowAsync(
        Guid installId,
        string packageName,
        PackageInstallStatus status,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var row = await db.PackageInstalls
                .FirstOrDefaultAsync(r =>
                    r.InstallId == installId && r.PackageName == packageName,
                    cancellationToken);
            if (row is not null)
            {
                row.Status = status;
                row.CompletedAt = DateTimeOffset.UtcNow;
                row.ErrorMessage = errorMessage;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update package_installs row for '{Package}' (install {InstallId}).",
                packageName, installId);
        }
    }
}

/// <summary>
/// Thrown when Phase 1 detects a cross-package reference that cannot be
/// resolved within the install batch or in already-installed packages
/// (ADR-0035 decision 14).
/// </summary>
public class PackageDepGraphException : Exception
{
    /// <summary>Initialises a new <see cref="PackageDepGraphException"/>.</summary>
    /// <param name="missingReferences">
    /// One entry per unresolvable reference. Each entry is the exact string
    /// from the ADR: <c>"package X references pkg/name, which is not in the
    /// install batch and not installed in this tenant"</c>.
    /// </param>
    public PackageDepGraphException(IReadOnlyList<string> missingReferences)
        : base(BuildMessage(missingReferences))
    {
        MissingReferences = missingReferences;
    }

    /// <summary>The unresolvable cross-package references.</summary>
    public IReadOnlyList<string> MissingReferences { get; }

    private static string BuildMessage(IReadOnlyList<string> refs)
        => string.Join("; ", refs);
}

/// <summary>
/// Thrown when Phase 1's name-collision pre-flight finds one or more
/// artefact names already registered in the tenant directory
/// (ADR-0035 decision 10).
/// </summary>
public class PackageNameCollisionException : Exception
{
    /// <summary>Initialises a new <see cref="PackageNameCollisionException"/>.</summary>
    /// <param name="collidingNames">The names that already exist in the directory.</param>
    public PackageNameCollisionException(IReadOnlyList<string> collidingNames)
        : base($"The following names already exist in the tenant: {string.Join(", ", collidingNames)}")
    {
        CollidingNames = collidingNames;
    }

    /// <summary>The names that caused the collision.</summary>
    public IReadOnlyList<string> CollidingNames { get; }
}

/// <summary>
/// <see cref="IPackageCatalogProvider"/> decorator that overlays in-flight
/// batch packages on top of the underlying file-system catalog. Used so
/// packages in a multi-package batch can resolve cross-package references
/// to each other before the batch has been committed (ADR-0035 decision 14).
/// </summary>
internal sealed class InFlightBatchCatalogProvider : IPackageCatalogProvider
{
    private readonly Dictionary<string, (InstallTarget Target, string PackageRoot)> _inFlight;
    private readonly IPackageCatalogProvider? _underlying;

    internal InFlightBatchCatalogProvider(
        Dictionary<string, (InstallTarget Target, string PackageRoot)> inFlight,
        IPackageCatalogProvider? underlying)
    {
        _inFlight = inFlight;
        _underlying = underlying;
    }

    /// <inheritdoc />
    public Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default)
    {
        if (_inFlight.ContainsKey(packageName))
        {
            return Task.FromResult(true);
        }
        return _underlying?.PackageExistsAsync(packageName, cancellationToken)
               ?? Task.FromResult(false);
    }

    /// <inheritdoc />
    public async Task<string?> LoadArtefactYamlAsync(
        string packageName,
        ArtefactKind kind,
        string artefactName,
        CancellationToken cancellationToken = default)
    {
        if (_inFlight.TryGetValue(packageName, out var inFlight))
        {
            // Resolve from the in-flight package's local directory.
            var subDir = kind switch
            {
                ArtefactKind.Unit => "units",
                ArtefactKind.Agent => "agents",
                ArtefactKind.Skill => "skills",
                ArtefactKind.Workflow => "workflows",
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var ext = kind == ArtefactKind.Skill ? ".md" : ".yaml";
            var path = System.IO.Path.Combine(inFlight.PackageRoot, subDir, artefactName + ext);
            if (System.IO.File.Exists(path))
            {
                return await System.IO.File.ReadAllTextAsync(path, cancellationToken);
            }
            return null;
        }

        if (_underlying is not null)
        {
            return await _underlying.LoadArtefactYamlAsync(
                packageName, kind, artefactName, cancellationToken);
        }

        return null;
    }
}