// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
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
    private readonly IRuntimeCatalog _runtimeCatalog;
    private readonly ILlmCredentialResolver _credentialResolver;
    private readonly ISecretStore _secretStore;
    private readonly ISecretRegistry _secretRegistry;
    private readonly ITenantContext _tenantContext;
    private readonly IPackageCatalogProvider? _catalogProvider;
    private readonly ILogger<PackageInstallService> _logger;

    /// <summary>
    /// Initialises a new <see cref="PackageInstallService"/>.
    /// </summary>
    public PackageInstallService(
        IServiceScopeFactory scopeFactory,
        IDirectoryService directoryService,
        IPackageArtefactActivator activator,
        IRuntimeCatalog runtimeCatalog,
        ILlmCredentialResolver credentialResolver,
        ISecretStore secretStore,
        ISecretRegistry secretRegistry,
        ITenantContext tenantContext,
        ILogger<PackageInstallService> logger,
        IPackageCatalogProvider? catalogProvider = null)
    {
        _scopeFactory = scopeFactory;
        _directoryService = directoryService;
        _activator = activator;
        _runtimeCatalog = runtimeCatalog;
        _credentialResolver = credentialResolver;
        _secretStore = secretStore;
        _secretRegistry = secretRegistry;
        _tenantContext = tenantContext;
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
        var resolvedBindings = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>>(
            StringComparer.OrdinalIgnoreCase);
        // #1679: per-package map of unit name → merged execution defaults.
        var resolvedExecutions = new Dictionary<string, IReadOnlyDictionary<string, ResolvedExecutionDefaults>>(
            StringComparer.OrdinalIgnoreCase);

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

        // ADR-0043 §6: resolve each target's `--into <unit>` to a parent
        // Guid (or null for tenant-scope). Runs before any DB writes so
        // an invalid scope binding fails the install fast with a 400.
        var resolvedScopes = await ResolveInstallScopesAsync(resolvedTargets, cancellationToken);

        // #1671: connector-binding pre-flight before any DB writes. Aggregate
        // every gap across every package in the batch into a single
        // ConnectorBindingsMissingException so the operator sees the full
        // list at once. UnknownSlugs (binding supplied for a slug the
        // package doesn't declare) becomes UnknownConnectorSlugException —
        // the install request was structurally wrong, not just incomplete.
        var allMissing = new List<ConnectorBindingMissing>();
        UnknownConnectorBindingEntry? firstUnknown = null;
        foreach (var (target, pkg) in resolvedTargets)
        {
            var resolution = ConnectorBindingResolver.Resolve(
                pkg, target.PackageBindings, target.UnitBindings);
            if (resolution.UnknownSlugs.Count > 0 && firstUnknown is null)
            {
                firstUnknown = resolution.UnknownSlugs[0];
            }
            allMissing.AddRange(resolution.Missing);
            resolvedBindings[pkg.Name] = resolution.Bindings;
        }
        if (firstUnknown is not null)
        {
            throw new UnknownConnectorSlugException(
                firstUnknown.Slug, firstUnknown.Scope, firstUnknown.UnitName);
        }
        if (allMissing.Count > 0)
        {
            throw new ConnectorBindingsMissingException(allMissing);
        }

        // #2159: LLM-credential pre-flight. Same shape as the connector
        // pre-flight: derive each unit's required (provider, authMethod)
        // edge from the runtime catalogue, match against operator-supplied
        // bindings, fall back to the tenant secret store, and aggregate
        // genuine gaps into a single CredentialsMissingException so the
        // operator sees the full punch-list at once. Resolved bindings
        // are deferred to a post-pre-flight write step (below) so a
        // missing-secret failure cannot leave half-written tenant
        // secrets behind.
        var allCredMissing = new List<CredentialMissing>();
        UnknownCredentialEdgeEntry? firstUnknownCred = null;
        var resolvedCredentials = new List<ResolvedCredentialBinding>();
        foreach (var (target, pkg) in resolvedTargets)
        {
            var required = CredentialBindingResolver.CollectRequired(pkg, _runtimeCatalog);
            var resolution = CredentialBindingResolver.Resolve(required, target.Credentials);
            if (resolution.UnknownEdges.Count > 0 && firstUnknownCred is null)
            {
                firstUnknownCred = resolution.UnknownEdges[0];
            }
            resolvedCredentials.AddRange(resolution.Resolved);

            foreach (var candidate in resolution.UnsuppliedCandidates)
            {
                var lookup = await _credentialResolver.ResolveAsync(
                    candidate.Provider,
                    candidate.AuthMethod,
                    agentId: null,
                    unitId: null,
                    cancellationToken);
                if (lookup.Source == LlmCredentialSource.NotFound)
                {
                    allCredMissing.Add(CredentialBindingResolver.ToMissing(candidate));
                }
            }
        }
        if (firstUnknownCred is not null)
        {
            throw new UnknownCredentialEdgeException(
                firstUnknownCred.Provider, firstUnknownCred.AuthMethod);
        }
        if (allCredMissing.Count > 0)
        {
            throw new CredentialsMissingException(allCredMissing);
        }

        // Persist any operator-supplied credentials as tenant secrets
        // before Phase 1 begins. Idempotent: if a secret already exists
        // under the canonical name, rotate its value forward (the
        // operator's explicit re-supply is treated as intent-to-override).
        // Done before Phase 1 so a write failure aborts the install
        // without staging rows; the registry itself is transactional with
        // EF, but the underlying SecretStore writes are not, so we accept
        // best-effort orphan blobs on a partial failure (cleaned up by
        // `spring secret delete` if needed).
        if (resolvedCredentials.Count > 0)
        {
            await PersistSuppliedCredentialsAsync(resolvedCredentials, cancellationToken);
        }

        // #1679: execution-defaults pre-flight. Merge each member unit's
        // own `execution:` against the package's `execution:` declaration
        // (when present and the unit is eligible to inherit) and fail
        // fast if any inheriting member ends up without a resolvable
        // image. Aggregated across the batch so the operator sees every
        // gap at once rather than dripping out activation failures one
        // Phase-2 dispatch at a time.
        var allExecMissing = new List<ExecutionConfigurationMissing>();
        foreach (var (_, pkg) in resolvedTargets)
        {
            var execResolution = ExecutionDefaultsResolver.Resolve(pkg);
            allExecMissing.AddRange(execResolution.Missing);
            resolvedExecutions[pkg.Name] = execResolution.ByUnit;
        }
        if (allExecMissing.Count > 0)
        {
            throw new ExecutionConfigurationsMissingException(allExecMissing);
        }

        // Topological sort of packages by cross-package reference order.
        // dep-provider packages come first so Phase 2 activates dependents
        // after their dependencies.
        var sorted = TopologicalSort(resolvedTargets);

        // #1629 PR7: mint a Guid per local artefact symbol up-front so the
        // staging row, the directory entry, and the activator all key off the
        // same identity. The map is keyed by package name so two packages can
        // share artefact names without colliding. Cross-package artefacts
        // already have a Guid identity in the referenced package's catalog;
        // they are not minted here.
        var symbolMap = new Dictionary<string, LocalSymbolMap>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, pkg) in sorted)
        {
            var map = new LocalSymbolMap();
            foreach (var unit in pkg.Units.Where(a => !a.IsCrossPackage))
            {
                map.GetOrMint(ArtefactKind.Unit, unit.Name);
            }
            foreach (var agent in pkg.Agents.Where(a => !a.IsCrossPackage))
            {
                map.GetOrMint(ArtefactKind.Agent, agent.Name);
            }
            symbolMap[pkg.Name] = map;
        }

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

                // Write unit_definitions staging rows. Identity is the row's
                // Guid id post-#1629; the human-readable name lives on
                // DisplayName only. The Guid is taken from the per-package
                // symbol map so the staging row and the directory entry the
                // activator later writes share a single identity (#1629 PR7).
                var pkgMap = symbolMap[pkg.Name];
                foreach (var unit in pkg.Units.Where(a => !a.IsCrossPackage))
                {
                    var unitId = pkgMap.GetOrMint(ArtefactKind.Unit, unit.Name);
                    var existing = await db.UnitDefinitions
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(u =>
                            u.DisplayName == unit.Name && u.DeletedAt == null,
                            cancellationToken);
                    if (existing is null)
                    {
                        var entity = new UnitDefinitionEntity
                        {
                            Id = unitId,
                            DisplayName = unit.Name,
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

                // #1671: persist the package-scope connector bindings as
                // tenant_connector_installs rows scoped to this install. Unit-
                // scope bindings ride through with the unit creation activator
                // and land on the unit's connector_definitions row, mirroring
                // the existing single-binding-per-unit shape.
                if (target.PackageBindings is { Count: > 0 } pkgBindings)
                {
                    foreach (var (slug, binding) in pkgBindings)
                    {
                        db.TenantConnectorInstalls.Add(new TenantConnectorInstallEntity
                        {
                            Id = Guid.NewGuid(),
                            ConnectorId = slug,
                            ConfigJson = binding.Config,
                            InstalledAt = now,
                            UpdatedAt = now,
                            PackageInstallId = installId,
                        });
                    }
                }
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
            var pkgBindings = resolvedBindings.TryGetValue(pkg.Name, out var rb)
                ? rb
                : null;
            var pkgExec = resolvedExecutions.TryGetValue(pkg.Name, out var re)
                ? re
                : null;
            var parentUnitId = resolvedScopes.TryGetValue(pkg.Name, out var p) ? p : null;
            var (outcome, error) = await ActivatePackageAsync(
                pkg, installId, symbolMap[pkg.Name], pkgBindings, pkgExec, cancellationToken);

            // ADR-0043 §6: re-bind top-level artefacts to the chosen
            // parent unit (when `--into` was supplied). Runs after the
            // activator wrote the default tenant-scope edges so the
            // operation is "edge swap" rather than "edge create" — the
            // activator's existing top-level logic stays untouched.
            if (outcome == PackageInstallOutcome.Active && parentUnitId.HasValue)
            {
                try
                {
                    await BindTopLevelArtefactsToParentAsync(
                        pkg, symbolMap[pkg.Name], parentUnitId.Value, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Phase 2: failed to bind package '{Package}' top-level artefacts to parent unit '{ParentId}' (install {InstallId}).",
                        pkg.Name, parentUnitId.Value, installId);
                    outcome = PackageInstallOutcome.Failed;
                    error ??= $"Failed to bind top-level artefacts to parent unit '{parentUnitId.Value}': {ex.Message}";
                }
            }

            // #2246: surface the artefact identities the install created so
            // clients (wizard, CLI) can take follow-up actions like
            // auto-starting units or auto-deploying persistent agents.
            var createdUnitNames = pkg.Units
                .Where(a => !a.IsCrossPackage)
                .Select(a => a.Name)
                .ToList();
            var createdAgentIds = pkg.Agents
                .Where(a => !a.IsCrossPackage)
                .Select(a => symbolMap[pkg.Name].GetOrMint(ArtefactKind.Agent, a.Name).ToString())
                .ToList();

            packageResults.Add(new PackageInstallResult(
                pkg.Name, outcome, error, createdUnitNames, createdAgentIds));

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

        // #2246: include the names of every unit / agent the install
        // created so clients polling status can still discover the
        // artefact identities after a refresh (e.g. for auto-start).
        // Re-parse each package's staged YAML to recover the declared
        // names; cross-reference the directory tables for the agent ids
        // minted at Phase 1.
        var packages = new List<PackageInstallResult>(rows.Count);
        foreach (var r in rows)
        {
            var outcome = r.Status switch
            {
                PackageInstallStatus.Active => PackageInstallOutcome.Active,
                PackageInstallStatus.Failed => PackageInstallOutcome.Failed,
                _ => PackageInstallOutcome.Staging,
            };

            IReadOnlyList<string> unitNames = Array.Empty<string>();
            IReadOnlyList<string> agentIds = Array.Empty<string>();
            try
            {
                var inputs = JsonSerializer.Deserialize<Dictionary<string, string>>(r.InputsJson)
                    ?? new Dictionary<string, string>();
                var pkg = await PackageManifestParser.ParseAndResolveAsync(
                    r.OriginalManifestYaml,
                    packageRoot: r.PackageRoot,
                    inputValues: inputs,
                    catalogProvider: _catalogProvider,
                    cancellationToken: cancellationToken);
                unitNames = pkg.Units
                    .Where(a => !a.IsCrossPackage)
                    .Select(a => a.Name)
                    .ToList();
                var collected = new List<string>();
                foreach (var agent in pkg.Agents.Where(a => !a.IsCrossPackage))
                {
                    var row = await db.AgentDefinitions
                        .IgnoreQueryFilters()
                        .FirstOrDefaultAsync(
                            a => a.DisplayName == agent.Name && a.DeletedAt == null,
                            cancellationToken);
                    if (row is not null)
                    {
                        collected.Add(row.Id.ToString());
                    }
                }
                agentIds = collected;
            }
            catch
            {
                // Re-parse failures during status read are non-fatal — the
                // caller still gets an accurate state.
            }

            packages.Add(new PackageInstallResult(
                r.PackageName,
                outcome,
                r.ErrorMessage,
                unitNames,
                agentIds));
        }

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
                // #2246: surface artefact identities even when the package
                // was already active so the retry response shape stays
                // uniform across packages in the batch.
                IReadOnlyList<string> activeUnitNames = Array.Empty<string>();
                IReadOnlyList<string> activeAgentIds = Array.Empty<string>();
                try
                {
                    var alreadyActiveInputs = JsonSerializer.Deserialize<Dictionary<string, string>>(row.InputsJson)
                        ?? new Dictionary<string, string>();
                    var alreadyActivePkg = await PackageManifestParser.ParseAndResolveAsync(
                        row.OriginalManifestYaml,
                        packageRoot: row.PackageRoot,
                        inputValues: alreadyActiveInputs,
                        catalogProvider: _catalogProvider,
                        cancellationToken: cancellationToken);
                    activeUnitNames = alreadyActivePkg.Units
                        .Where(a => !a.IsCrossPackage)
                        .Select(a => a.Name)
                        .ToList();
                    var activeMap = await BuildSymbolMapFromStagingAsync(alreadyActivePkg, installId, cancellationToken);
                    activeAgentIds = alreadyActivePkg.Agents
                        .Where(a => !a.IsCrossPackage)
                        .Select(a => activeMap.GetOrMint(ArtefactKind.Agent, a.Name).ToString())
                        .ToList();
                }
                catch
                {
                    // Best-effort; the row's stored YAML may be malformed
                    // on disk but the install row itself is still valid.
                }
                packageResults.Add(new PackageInstallResult(
                    row.PackageName, PackageInstallOutcome.Active, null,
                    activeUnitNames, activeAgentIds));
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
                    packageRoot: row.PackageRoot,
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

            // Rebuild the local-symbol map from the staging rows so the
            // retry uses the same Guids that Phase 1 minted on the original
            // install. Looking the rows up by display-name keeps the symbol
            // map deterministic across retries — every artefact resolves to
            // its previously-minted id rather than getting a fresh one.
            var retryMap = await BuildSymbolMapFromStagingAsync(pkg, installId, cancellationToken);

            // #1671: rehydrate the package-scope bindings from
            // tenant_connector_installs so retry resolves the same per-unit
            // bindings the original install computed. Unit-scope overrides
            // (which land on the per-unit connector store via Phase 2) do
            // not need rehydration here — they are already on the unit row.
            var rehydratedPackageBindings = await LoadPackageScopeBindingsAsync(installId, cancellationToken);
            var rehydratedResolution = ConnectorBindingResolver.Resolve(
                pkg, rehydratedPackageBindings, unitBindings: null);

            // #1679: re-run the execution resolver from the parsed YAML
            // so retries apply the same merged defaults the original
            // install computed. The merge is pure on top of the manifest
            // — no DB round-trip needed; the package YAML stored on the
            // install row carries every input the resolver requires.
            var rehydratedExec = ExecutionDefaultsResolver.Resolve(pkg);

            var (outcome, error) = await ActivatePackageAsync(
                pkg, installId, retryMap, rehydratedResolution.Bindings, rehydratedExec.ByUnit, cancellationToken);

            // #2246: surface created artefact identities so clients can
            // take post-retry actions (auto-start / auto-deploy).
            var retryUnitNames = pkg.Units
                .Where(a => !a.IsCrossPackage)
                .Select(a => a.Name)
                .ToList();
            var retryAgentIds = pkg.Agents
                .Where(a => !a.IsCrossPackage)
                .Select(a => retryMap.GetOrMint(ArtefactKind.Agent, a.Name).ToString())
                .ToList();

            packageResults.Add(new PackageInstallResult(
                row.PackageName, outcome, error, retryUnitNames, retryAgentIds));

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

            // #1671: drop the package-scope and unit-scope connector binding
            // rows owned by this install. Tenant-level rows (no
            // package_install_id) are left intact — they predate / outlive
            // the install.
            var bindingRows = await db.TenantConnectorInstalls
                .IgnoreQueryFilters()
                .Where(b => b.PackageInstallId == installId)
                .ToListAsync(cancellationToken);
            db.TenantConnectorInstalls.RemoveRange(bindingRows);

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

        // ADR-0043 §5: a single resolver instance covers every target in
        // the batch — chained / cross-package templates resolve against
        // the in-flight overlay catalog so a package can `from:` a
        // template defined by another package in the same install.
        var templateResolver = new TemplateResolver(overlayCatalog);

        foreach (var target in targets)
        {
            var parsed = await PackageManifestParser.ParseAndResolveAsync(
                target.OriginalYaml,
                packageRoot: target.PackageRoot,
                inputValues: target.Inputs,
                catalogProvider: overlayCatalog,
                cancellationToken: cancellationToken);

            // ADR-0043 §5: stamp `from:` references before any
            // downstream pipeline step consumes the resolved package.
            // The stamped tree replaces every `from:` reference with
            // its merged-body concrete artefact plus cloned children;
            // templates themselves drop out.
            var stamped = await templateResolver.ResolveAsync(
                parsed, target.PackageRoot, cancellationToken);

            result.Add((target, stamped));
        }

        // Validate dep-graph closure: every cross-package reference must resolve
        // to a package in this batch or to an already-installed package.
        await ValidateDepGraphClosureAsync(result, cancellationToken);

        return result;
    }

    /// <summary>
    /// Resolves each install target's optional <c>IntoUnit</c> reference
    /// (ADR-0043 §6) to a parent unit Guid (or <c>null</c> for the
    /// tenant-scope default). Rejects ambiguous / unknown references
    /// before any DB writes so the install fails fast with a clean 400.
    /// </summary>
    private async Task<Dictionary<string, Guid?>> ResolveInstallScopesAsync(
        List<(InstallTarget Target, ResolvedPackage Package)> resolvedTargets,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        // Build a set of package names in the batch so we can reject
        // `--into <package-name>` early without a DB round-trip.
        var batchPackageNames = new HashSet<string>(
            resolvedTargets.Select(r => r.Package.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (target, pkg) in resolvedTargets)
        {
            var raw = target.IntoUnit?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                result[pkg.Name] = null;  // tenant-scope default
                continue;
            }

            // Explicit "tenant" — the readable form of the default.
            if (string.Equals(raw, "tenant", StringComparison.OrdinalIgnoreCase))
            {
                result[pkg.Name] = null;
                continue;
            }

            // Reject `--into <package-name>` — packages don't contain
            // other packages' artefacts (ADR-0043 §6).
            if (batchPackageNames.Contains(raw))
            {
                throw new InvalidInstallScopeException(
                    $"--into '{raw}' is rejected: '{raw}' is a package name in this install batch, " +
                    "not a unit. Packages don't contain other packages' artefacts. " +
                    "Pass a unit display name or unit id, or omit --into for the tenant default.");
            }

            // Lookup by Guid first; fall back to display-name lookup.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            Guid? resolved = null;
            if (Guid.TryParse(raw, out var asGuid)
                || Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(raw, out asGuid))
            {
                var byId = await db.UnitDefinitions
                    .AsNoTracking()
                    .Where(u => u.Id == asGuid && u.DeletedAt == null)
                    .FirstOrDefaultAsync(cancellationToken);
                if (byId is not null)
                {
                    resolved = byId.Id;
                }
            }

            if (resolved is null)
            {
                var matches = await db.UnitDefinitions
                    .AsNoTracking()
                    .Where(u => u.DisplayName == raw && u.DeletedAt == null)
                    .ToListAsync(cancellationToken);
                if (matches.Count == 1)
                {
                    resolved = matches[0].Id;
                }
                else if (matches.Count > 1)
                {
                    throw new InvalidInstallScopeException(
                        $"--into '{raw}' is ambiguous: {matches.Count} units share that display name. " +
                        "Pass the unit's Guid id instead.");
                }
            }

            if (resolved is null)
            {
                throw new InvalidInstallScopeException(
                    $"--into '{raw}' did not resolve to a known unit in this tenant. " +
                    "Pass an existing unit's display name or Guid, or omit --into for the tenant default.");
            }

            result[pkg.Name] = resolved;
        }

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
        // Build dependency map: packageName → set of packages it depends on
        // (only within-batch deps count; external deps are resolved by Phase 2
        // ordering within each package).
        var byName = new Dictionary<string, (InstallTarget Target, ResolvedPackage Package)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            byName[item.Package.Name] = item;
        }

        // Second pass: build deps map with byName fully populated so that
        // IntersectWith sees ALL batch packages, not just those seen so far.
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
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

        // Kahn's algorithm. inDegree[X] = number of packages X depends on
        // within this batch (i.e. X's in-edges in the dependency DAG where an
        // edge A→B means "A must be activated after B"). Packages with
        // inDegree 0 have no batch-internal dependencies and are activated
        // first.
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in byName.Keys) inDegree[name] = 0;
        foreach (var (name, depSet) in deps)
        {
            // Each dep in depSet is a package that 'name' depends on, so
            // 'name' has one more incoming dependency edge.
            foreach (var dep in depSet)
            {
                if (inDegree.ContainsKey(dep))
                {
                    inDegree[name]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<(InstallTarget, ResolvedPackage)>();

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(byName[node]);

            // 'node' has been placed. Reduce the in-degree of every package
            // that depends on 'node'; once their count hits 0, all their
            // dependencies have been placed and they can be enqueued.
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
        // Under #1629 every artefact's identity is a fresh Guid minted at
        // install time, not its display name; the directory is keyed by Guid
        // and offers no name → entry resolver. Name-collision pre-flight
        // therefore queries the staging tables directly: an in-tenant unit
        // (display-name match, not soft-deleted) means the install would
        // produce a confusing duplicate, even though Guid identity would be
        // distinct.
        var collisions = new List<string>();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        foreach (var (_, pkg) in sorted)
        {
            foreach (var unit in pkg.Units.Where(a => !a.IsCrossPackage))
            {
                var nameTaken = await db.UnitDefinitions
                    .AnyAsync(
                        u => u.DisplayName == unit.Name && u.DeletedAt == null,
                        cancellationToken);
                if (nameTaken)
                {
                    collisions.Add(unit.Name);
                }
            }

            foreach (var agent in pkg.Agents.Where(a => !a.IsCrossPackage))
            {
                var nameTaken = await db.AgentDefinitions
                    .AnyAsync(
                        a => a.DisplayName == agent.Name && a.DeletedAt == null,
                        cancellationToken);
                if (nameTaken)
                {
                    collisions.Add(agent.Name);
                }
            }
        }

        if (collisions.Count > 0)
        {
            throw new PackageNameCollisionException(collisions);
        }
    }

    /// <summary>
    /// Reconstructs a <see cref="LocalSymbolMap"/> for a retry by reading
    /// the staging rows that the original install wrote. Each artefact is
    /// re-bound to its existing Guid so re-running activation does not
    /// create a duplicate entity with a different id.
    /// </summary>
    private async Task<LocalSymbolMap> BuildSymbolMapFromStagingAsync(
        ResolvedPackage pkg,
        Guid installId,
        CancellationToken cancellationToken)
    {
        var map = new LocalSymbolMap();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        foreach (var unit in pkg.Units.Where(a => !a.IsCrossPackage))
        {
            var row = await db.UnitDefinitions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    u => u.InstallId == installId && u.DisplayName == unit.Name,
                    cancellationToken);
            if (row is not null)
            {
                map.Bind(ArtefactKind.Unit, unit.Name, row.Id);
            }
            else
            {
                _ = map.GetOrMint(ArtefactKind.Unit, unit.Name);
            }
        }

        foreach (var agent in pkg.Agents.Where(a => !a.IsCrossPackage))
        {
            var row = await db.AgentDefinitions
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    a => a.DisplayName == agent.Name,
                    cancellationToken);
            if (row is not null)
            {
                map.Bind(ArtefactKind.Agent, agent.Name, row.Id);
            }
            else
            {
                _ = map.GetOrMint(ArtefactKind.Agent, agent.Name);
            }
        }

        return map;
    }

    /// <summary>
    /// Reloads the package-scope connector bindings persisted by Phase 1
    /// for the given install. Used by <see cref="RetryAsync"/> so a retry
    /// recomputes per-unit inheritance against the same operator-supplied
    /// bindings (the request body is not retained server-side).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, ConnectorBinding>> LoadPackageScopeBindingsAsync(
        Guid installId,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.TenantConnectorInstalls
            .IgnoreQueryFilters()
            .Where(e => e.PackageInstallId == installId && e.UnitId == null)
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var config = row.ConfigJson ?? JsonDocument.Parse("{}").RootElement;
            result[row.ConnectorId] = new ConnectorBinding(row.ConnectorId, config);
        }
        return result;
    }

    /// <summary>
    /// #2159: persist operator-supplied LLM credentials at tenant scope.
    /// Per credential: write the value to the secret store, then either
    /// register a new secret-registry entry or rotate the existing one
    /// forward to the new store key. Idempotent on re-supply (the
    /// operator's explicit re-paste counts as intent-to-rotate).
    /// </summary>
    private async Task PersistSuppliedCredentialsAsync(
        IReadOnlyList<ResolvedCredentialBinding> resolvedCredentials,
        CancellationToken cancellationToken)
    {
        var ownerId = _tenantContext.CurrentTenantId;
        foreach (var resolved in resolvedCredentials)
        {
            var secretRef = new SecretRef(SecretScope.Tenant, ownerId, resolved.Required.SecretName);
            var storeKey = await _secretStore.WriteAsync(resolved.Binding.Value, cancellationToken);
            try
            {
                var existing = await _secretRegistry.LookupAsync(secretRef, cancellationToken);
                if (existing is null)
                {
                    await _secretRegistry.RegisterAsync(
                        secretRef,
                        storeKey,
                        SecretOrigin.PlatformOwned,
                        propagate: true,
                        cancellationToken);
                }
                else
                {
                    await _secretRegistry.RotateAsync(
                        secretRef,
                        storeKey,
                        SecretOrigin.PlatformOwned,
                        deletePreviousStoreKeyAsync: null,
                        cancellationToken);
                }
            }
            catch
            {
                // Registry write failed after the store blob was written.
                // Best-effort cleanup of the orphan blob; surface the
                // primary registry error so the install aborts.
                try
                {
                    await _secretStore.DeleteAsync(storeKey, CancellationToken.None);
                }
                catch
                {
                    // Swallow — reconciliation handles orphans.
                }
                throw;
            }
        }
    }

    // ── Phase 2 helpers ────────────────────────────────────────────────────

    /// <summary>
    /// ADR-0043 §6: re-binds the package's top-level Units / Agents to
    /// <paramref name="parentUnitId"/>. The activator has already written
    /// the default tenant-scope edge for each top-level Unit; this method
    /// replaces those edges with parent-edges and adds the
    /// <c>unit_memberships</c> rows that put top-level Agents inside the
    /// chosen parent. Nested artefacts (a top-level unit's own members,
    /// an agent's own skills) are unaffected.
    /// </summary>
    private async Task BindTopLevelArtefactsToParentAsync(
        ResolvedPackage pkg,
        LocalSymbolMap symbolMap,
        Guid parentUnitId,
        CancellationToken cancellationToken)
    {
        var tenantId = _tenantContext.CurrentTenantId;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        foreach (var unit in pkg.Units.Where(u => u.IsTopLevel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!symbolMap.TryResolve(ArtefactKind.Unit, unit.Name, out var childId))
            {
                continue;
            }

            // Retire the tenant-root edge if the activator wrote one.
            var tenantEdge = await db.UnitSubunitMemberships
                .FirstOrDefaultAsync(
                    e => e.ParentId == tenantId && e.ChildId == childId,
                    cancellationToken);
            if (tenantEdge is not null)
            {
                db.UnitSubunitMemberships.Remove(tenantEdge);
            }

            // Upsert the parent-edge.
            var existing = await db.UnitSubunitMemberships
                .FirstOrDefaultAsync(
                    e => e.ParentId == parentUnitId && e.ChildId == childId,
                    cancellationToken);
            if (existing is null)
            {
                db.UnitSubunitMemberships.Add(new Cvoya.Spring.Dapr.Data.Entities.UnitSubunitMembershipEntity
                {
                    ParentId = parentUnitId,
                    ChildId = childId,
                });
            }
        }

        foreach (var agent in pkg.Agents.Where(a => a.IsTopLevel))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!symbolMap.TryResolve(ArtefactKind.Agent, agent.Name, out var agentId))
            {
                continue;
            }

            var existing = await db.UnitMemberships
                .FirstOrDefaultAsync(
                    m => m.UnitId == parentUnitId && m.AgentId == agentId,
                    cancellationToken);
            if (existing is null)
            {
                db.UnitMemberships.Add(new Cvoya.Spring.Dapr.Data.Entities.UnitMembershipEntity
                {
                    UnitId = parentUnitId,
                    AgentId = agentId,
                    Enabled = true,
                    IsPrimary = true,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(PackageInstallOutcome Outcome, string? Error)> ActivatePackageAsync(
        ResolvedPackage pkg,
        Guid installId,
        LocalSymbolMap symbolMap,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>? perUnitBindings,
        IReadOnlyDictionary<string, ResolvedExecutionDefaults>? perUnitExecution,
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
            IReadOnlyDictionary<string, ConnectorBinding>? unitBindings = null;
            if (artefact.Kind == ArtefactKind.Unit
                && perUnitBindings is not null
                && perUnitBindings.TryGetValue(artefact.Name, out var b))
            {
                unitBindings = b;
            }

            ResolvedExecutionDefaults? unitExecution = null;
            if (artefact.Kind == ArtefactKind.Unit
                && perUnitExecution is not null
                && perUnitExecution.TryGetValue(artefact.Name, out var e))
            {
                unitExecution = e;
            }

            try
            {
                await _activator.ActivateAsync(
                    pkg.Name, artefact, installId, symbolMap, unitBindings, unitExecution, cancellationToken);
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
                    u.InstallId == installId && u.DisplayName == artefact.Name,
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
                        u.InstallId == installId && u.DisplayName == artefact.Name,
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
/// Thrown when the install pipeline rejects an <c>--into &lt;unit&gt;</c>
/// binding (ADR-0043 §6). Covers: unknown unit reference, unit reference
/// that resolved to a package name (operator confusion), or empty
/// reference string. Surfaces as ProblemDetails 400.
/// </summary>
public class InvalidInstallScopeException : Exception
{
    /// <summary>Initialises a new <see cref="InvalidInstallScopeException"/>.</summary>
    public InvalidInstallScopeException(string message) : base(message) { }
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
