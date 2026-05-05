// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Maps package install/status/retry/abort endpoints (ADR-0035 decision 11).
///
/// <list type="bullet">
///   <item><description><c>POST /api/v1/packages/install</c> — install one or more packages as a batch.</description></item>
///   <item><description><c>POST /api/v1/packages/install/file</c> — install from uploaded YAML (browse path, ADR-0035 decision 13).</description></item>
///   <item><description><c>GET /api/v1/installs/{id}</c> — inspect install status including per-package detail.</description></item>
///   <item><description><c>POST /api/v1/installs/{id}/retry</c> — re-run Phase 2 for a failed install.</description></item>
///   <item><description><c>POST /api/v1/installs/{id}/abort</c> — discard staging rows for a failed install.</description></item>
/// </list>
///
/// All five endpoints are thin adapters over <see cref="IPackageInstallService"/>.
/// Error mapping follows ADR-0035 decisions 10/11 and the issue acceptance criteria:
/// <list type="bullet">
///   <item><description>Phase-1 dep-graph closure violation (<see cref="PackageDepGraphException"/>) → 400.</description></item>
///   <item><description>Phase-1 name collision (<see cref="PackageNameCollisionException"/>) → 409.</description></item>
///   <item><description>Phase-1 parse/validation errors → 400.</description></item>
///   <item><description>Phase-2 activation failure → 201 with <c>status=failed</c>.</description></item>
/// </list>
///
/// <para>
/// Phase-2 status code decision: Phase 2 runs synchronously in
/// <see cref="IPackageInstallService.InstallAsync"/> (best-effort activation
/// after Phase-1 commit). The endpoint always returns 201 Created because
/// Phase 1 committed; the body's <c>status</c> field carries <c>failed</c>
/// when any activation failed, giving operators the install-id they need to
/// call <c>GET /installs/{id}</c>, <c>/retry</c>, or <c>/abort</c>.
/// </para>
/// </summary>
public static class PackageInstallEndpoints
{
    /// <summary>
    /// Registers package install endpoints on the supplied route builder.
    /// Returns a <see cref="RouteGroupBuilder"/> so callers can chain
    /// <c>.RequireAuthorization(...)</c> or other group-level configuration.
    /// </summary>
    public static RouteGroupBuilder MapPackageInstallEndpoints(this IEndpointRouteBuilder app)
    {
        // Use an empty prefix — the individual routes declare their full paths.
        // Grouping lets Program.cs apply a single .RequireAuthorization() call
        // that covers all five endpoints, consistent with how MapPackageEndpoints
        // and MapUnitEndpoints are wired.
        var group = app.MapGroup(string.Empty)
            .WithTags("PackageInstall");

        // ── POST /api/v1/packages/install ──────────────────────────────────
        group.MapPost("/api/v1/packages/install", InstallPackagesAsync)
            .WithName("InstallPackages")
            .WithSummary("Install one or more packages as a single atomic batch")
            .WithDescription(
                "Phase 1 (single EF transaction): validate, topo-sort, collision pre-flight, write staging rows. " +
                "Phase 2 (post-commit): activate actors in dependency order. Returns 201 with the install status. " +
                "Phase-2 failures appear as status=failed in the body; use GET /api/v1/installs/{id} for detail.")
            .Accepts<PackageInstallRequest>("application/json")
            .Produces<InstallStatusResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // ── POST /api/v1/packages/install/file ────────────────────────────
        group.MapPost("/api/v1/packages/install/file", InstallPackageFromFileAsync)
            .WithName("InstallPackageFromFile")
            .WithSummary("Install a package from an uploaded YAML file (browse path, ADR-0035 decision 13)")
            .WithDescription(
                "Accepts a multipart/form-data upload containing the package YAML. " +
                "For v0.1 the upload is one-shot: install and discard " +
                "(no persistent tenant-scoped catalog). Same response shape as InstallPackages.")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<InstallStatusResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .DisableAntiforgery();

        // ── GET /api/v1/installs/{id} ──────────────────────────────────────
        group.MapGet("/api/v1/installs/{id:guid}", GetInstallStatusAsync)
            .WithName("GetInstallStatus")
            .WithSummary("Get install status, including per-package detail")
            .Produces<InstallStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ── POST /api/v1/installs/{id}/retry ──────────────────────────────
        group.MapPost("/api/v1/installs/{id:guid}/retry", RetryInstallAsync)
            .WithName("RetryInstall")
            .WithSummary("Re-run Phase 2 for a failed install")
            .WithDescription(
                "Re-activates every package whose state is not yet active. " +
                "Phase 1 rows stay intact. Returns 200 with the updated status.")
            .Produces<InstallStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // ── POST /api/v1/installs/{id}/abort ──────────────────────────────
        group.MapPost("/api/v1/installs/{id:guid}/abort", AbortInstallAsync)
            .WithName("AbortInstall")
            .WithSummary("Discard all staging rows for a failed install")
            .WithDescription(
                "Deletes every row in package_installs, unit_definitions, " +
                "connector_definitions, and tenant_skill_bundle_bindings for this " +
                "install_id. Runs in a single EF transaction. After abort the install is gone.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private static async Task<IResult> InstallPackagesAsync(
        [FromBody] PackageInstallRequest request,
        [FromServices] IPackageInstallService installService,
        [FromServices] IPackageCatalogService catalogService,
        [FromServices] PackageCatalogOptions catalogOptions,
        CancellationToken cancellationToken)
    {
        if (request.Targets is null || request.Targets.Count == 0)
        {
            return Results.Problem(
                detail: "At least one install target is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Resolve YAML for each catalog-sourced target.
        List<InstallTarget> targets;
        try
        {
            targets = await ResolveTargetsFromCatalogAsync(
                request.Targets, catalogService, catalogOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is PackageParseException
            or PackageReferenceNotFoundException
            or PackageInputValidationException
            or PackageCycleException)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return await ExecuteInstallAsync(installService, targets, cancellationToken);
    }

    private static async Task<IResult> InstallPackageFromFileAsync(
        IFormFile? file,
        [FromServices] IPackageInstallService installService,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Results.Problem(
                detail: "A non-empty YAML file must be uploaded.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string yaml;
        using (var reader = new System.IO.StreamReader(file.OpenReadStream()))
        {
            yaml = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Results.Problem(
                detail: "The uploaded file is empty.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // For the file upload path we parse the YAML once just to extract
        // the package name, then pass the raw YAML through as OriginalYaml.
        // For v0.1 no packageRoot is needed — the YAML must be self-contained
        // (no within-package artefact file references; ADR-0035 decision 13).
        string packageName;
        try
        {
            var manifest = PackageManifestParser.ParseRaw(yaml);
            packageName = manifest.Metadata?.Name
                ?? throw new PackageParseException("Package manifest is missing metadata.name.");
        }
        catch (PackageParseException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        var targets = new List<InstallTarget>
        {
            new InstallTarget(
                PackageName: packageName,
                Inputs: new Dictionary<string, string>(),
                OriginalYaml: yaml,
                PackageRoot: null),
        };

        return await ExecuteInstallAsync(installService, targets, cancellationToken);
    }

    private static async Task<IResult> GetInstallStatusAsync(
        Guid id,
        [FromServices] IPackageInstallService installService,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var status = await installService.GetStatusAsync(id, cancellationToken);
        if (status is null)
        {
            return Results.Problem(
                detail: $"Install '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Enrich with timestamps from the package_installs rows.
        var rows = await db.PackageInstalls
            .Where(r => r.InstallId == id)
            .ToListAsync(cancellationToken);

        var startedAt = rows.Count > 0 ? rows.Min(r => r.StartedAt) : (DateTimeOffset?)null;
        var completedAt = rows.All(r => r.CompletedAt.HasValue)
            ? rows.Max(r => r.CompletedAt)
            : null;

        return Results.Ok(BuildStatusResponse(id, status.Packages, startedAt, completedAt, error: null));
    }

    private static async Task<IResult> RetryInstallAsync(
        Guid id,
        [FromServices] IPackageInstallService installService,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        // Check the install exists first so we can return 404 vs 409.
        var existing = await installService.GetStatusAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"Install '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // 409 if already fully active — nothing to retry.
        if (existing.Packages.All(p => p.Status == PackageInstallOutcome.Active))
        {
            return Results.Problem(
                detail: $"Install '{id}' is already fully active.",
                statusCode: StatusCodes.Status409Conflict);
        }

        InstallResult result;
        try
        {
            result = await installService.RetryAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }

        var rows = await db.PackageInstalls
            .Where(r => r.InstallId == id)
            .ToListAsync(cancellationToken);
        var startedAt = rows.Count > 0 ? rows.Min(r => r.StartedAt) : (DateTimeOffset?)null;
        var completedAt = rows.All(r => r.CompletedAt.HasValue)
            ? rows.Max(r => r.CompletedAt)
            : null;

        return Results.Ok(BuildStatusResponse(id, result.PackageResults, startedAt, completedAt, error: null));
    }

    private static async Task<IResult> AbortInstallAsync(
        Guid id,
        [FromServices] IPackageInstallService installService,
        CancellationToken cancellationToken)
    {
        // Check the install exists first.
        var existing = await installService.GetStatusAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.Problem(
                detail: $"Install '{id}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            await installService.AbortAsync(id, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.NoContent();
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Executes an install via the service, maps exceptions to problem-details,
    /// and returns the appropriate HTTP result.
    /// </summary>
    private static async Task<IResult> ExecuteInstallAsync(
        IPackageInstallService installService,
        List<InstallTarget> targets,
        CancellationToken cancellationToken)
    {
        InstallResult result;
        try
        {
            result = await installService.InstallAsync(targets, cancellationToken);
        }
        catch (ConnectorBindingsMissingException ex)
        {
            // #1671: structured 400 — the wizard / CLI render one row per
            // missing slug rather than parsing the prose detail string.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/connector-binding-missing",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "ConnectorBindingMissing",
                    ["missing"] = ex.Missing
                        .Select(m => new ConnectorBindingMissingDetail(m.Slug, m.Scope, m.UnitName))
                        .ToList(),
                });
        }
        catch (UnknownConnectorSlugException ex)
        {
            // #1671: a binding was supplied for a slug the package does not
            // declare. Structurally invalid; surface as 400 with the slug.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/unknown-connector-slug",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "UnknownConnectorSlug",
                    ["slug"] = ex.Slug,
                    ["scope"] = ex.Scope,
                    ["unitName"] = ex.UnitName,
                });
        }
        catch (PackageDepGraphException ex)
        {
            // ADR-0035 decision 14: dep-graph closure violations carry the
            // exact operator-actionable messages from the validator.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PackageNameCollisionException ex)
        {
            // ADR-0035 decision 10: name collision → 409.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (PackageParseException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PackageReferenceNotFoundException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PackageInputValidationException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (PackageCycleException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Phase 2 is synchronous-best-effort in InstallAsync (see service
        // implementation). The endpoint always returns 201 Created because
        // Phase 1 committed successfully; the body's status field carries
        // "failed" when any activation failed, giving operators the install-id
        // they need to call GET /installs/{id}, /retry, or /abort.
        var response = BuildStatusResponse(
            result.InstallId,
            result.PackageResults,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            error: null);

        return Results.Created(
            $"/api/v1/installs/{result.InstallId}",
            response);
    }

    /// <summary>
    /// Resolves catalog YAML for each install target supplied in a
    /// <see cref="PackageInstallRequest"/>. The catalog provides the raw
    /// <c>package.yaml</c> text; the package root for within-package artefact
    /// resolution is derived from <paramref name="catalogOptions"/>.
    /// </summary>
    private static async Task<List<InstallTarget>> ResolveTargetsFromCatalogAsync(
        IReadOnlyList<PackageInstallTarget> requestTargets,
        IPackageCatalogService catalogService,
        PackageCatalogOptions catalogOptions,
        CancellationToken cancellationToken)
    {
        var result = new List<InstallTarget>(requestTargets.Count);

        foreach (var t in requestTargets)
        {
            // Load the package YAML from the catalog.
            var yaml = await catalogService.LoadPackageManifestYamlAsync(t.PackageName, cancellationToken);
            if (yaml is null)
            {
                throw new KeyNotFoundException(
                    $"Package '{t.PackageName}' was not found in the catalog. " +
                    $"Run 'spring package list' to see available packages.");
            }

            // Derive the on-disk package root so the manifest parser can
            // resolve within-package artefact file references (unit YAMLs, etc.).
            // The catalog root is set via Packages:Root or auto-discovered.
            var packageRoot = string.IsNullOrWhiteSpace(catalogOptions.Root)
                ? null
                : System.IO.Path.Combine(catalogOptions.Root, t.PackageName);

            // #1671: project the wire connector-binding payload into the
            // service-layer ConnectorBinding shape.
            var (pkgBindings, unitBindings) = ProjectConnectorBindings(t.ConnectorBindings);

            result.Add(new InstallTarget(
                PackageName: t.PackageName,
                Inputs: t.Inputs ?? new Dictionary<string, string>(),
                OriginalYaml: yaml,
                PackageRoot: packageRoot,
                PackageBindings: pkgBindings,
                UnitBindings: unitBindings));
        }

        return result;
    }

    /// <summary>
    /// Projects the wire connector-binding payload (#1671) into the
    /// service-layer <see cref="Cvoya.Spring.Manifest.ConnectorBinding"/>
    /// shape consumed by <see cref="IPackageInstallService"/>.
    /// </summary>
    private static (
        IReadOnlyDictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>? Package,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>>? Units
    ) ProjectConnectorBindings(PackageConnectorBindings? bindings)
    {
        if (bindings is null)
        {
            return (null, null);
        }

        Dictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>? pkg = null;
        if (bindings.Package is { Count: > 0 } pkgIn)
        {
            pkg = new Dictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var (slug, payload) in pkgIn)
            {
                pkg[slug] = new Cvoya.Spring.Manifest.ConnectorBinding(slug, payload.Config);
            }
        }

        Dictionary<string, IReadOnlyDictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>>? units = null;
        if (bindings.Units is { Count: > 0 } unitsIn)
        {
            units = new Dictionary<string, IReadOnlyDictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (unitName, perUnit) in unitsIn)
            {
                var inner = new Dictionary<string, Cvoya.Spring.Manifest.ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
                foreach (var (slug, payload) in perUnit)
                {
                    inner[slug] = new Cvoya.Spring.Manifest.ConnectorBinding(slug, payload.Config);
                }
                units[unitName] = inner;
            }
        }
        return (pkg, units);
    }

    /// <summary>
    /// Builds an <see cref="InstallStatusResponse"/> from a list of
    /// per-package results. The aggregate status is:
    /// <c>active</c> if all packages succeeded,
    /// <c>failed</c> if any failed,
    /// <c>staging</c> otherwise.
    /// </summary>
    private static InstallStatusResponse BuildStatusResponse(
        Guid installId,
        IReadOnlyList<PackageInstallResult> packages,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? error)
    {
        var aggregateStatus = packages.All(p => p.Status == PackageInstallOutcome.Active)
            ? "active"
            : packages.Any(p => p.Status == PackageInstallOutcome.Failed)
                ? "failed"
                : "staging";

        var details = packages
            .Select(p => new InstallPackageDetail(
                p.PackageName,
                p.Status switch
                {
                    PackageInstallOutcome.Active => "active",
                    PackageInstallOutcome.Failed => "failed",
                    _ => "staging",
                },
                p.ErrorMessage))
            .ToList();

        return new InstallStatusResponse(
            installId,
            aggregateStatus,
            details,
            startedAt,
            completedAt,
            error);
    }
}