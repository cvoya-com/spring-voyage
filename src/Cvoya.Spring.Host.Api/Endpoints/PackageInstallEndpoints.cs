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
/// Error mapping follows ADR-0035 decision 11 and the issue acceptance criteria:
/// <list type="bullet">
///   <item><description>Phase-1 dep-graph closure violation (<see cref="PackageDepGraphException"/>) → 400.</description></item>
///   <item><description>Phase-1 ambiguous display-name override (#2310) → 400 with <c>code: AmbiguousDisplayName</c>.</description></item>
///   <item><description>Phase-1 parse/validation errors → 400.</description></item>
///   <item><description>Phase-2 activation failure → 201 with <c>status=failed</c>.</description></item>
/// </list>
/// Per ADR-0036 names are presentation-only — there is no 409 name-collision
/// pre-flight (#2310). Installing the same package multiple times produces
/// distinct rows with distinct Guids that may share a display name.
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
                "Phase 1 (single EF transaction): validate, topo-sort, write staging rows. " +
                "Phase 2 (post-commit): activate actors in dependency order. Returns 201 with the install status. " +
                "Phase-2 failures appear as status=failed in the body; use GET /api/v1/installs/{id} for detail.")
            .Accepts<PackageInstallRequest>("application/json")
            .Produces<InstallStatusResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest);

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
            packageName = manifest.Name
                ?? throw new PackageParseException("Package manifest is missing the required top-level 'name:' field.");
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
        catch (ExecutionConfigurationsMissingException ex)
        {
            // #1679: structured 400 — the wizard / CLI render one row per
            // unit missing an execution image rather than parsing the
            // prose detail string. Mirrors the ConnectorBindingMissing
            // shape from #1671 — same payload model, same code field, so
            // existing client error handling generalises.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/configuration-incomplete",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "ConfigurationIncomplete",
                    ["missing"] = ex.Missing
                        .Select(m => new ExecutionConfigurationMissingDetail(m.UnitName, m.Field))
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
        catch (CredentialsMissingException ex)
        {
            // #2159: structured 400 — the wizard / CLI render one row per
            // missing credential rather than parsing the prose detail
            // string. Mirrors the ConnectorBindingMissing shape.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/credentials-missing",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "CredentialsMissing",
                    ["missing"] = ex.Missing
                        .Select(m => new CredentialMissingDetail(
                            m.Provider,
                            FormatAuthMethod(m.AuthMethod),
                            m.SecretName,
                            m.CredentialEnvVar,
                            m.Scope,
                            m.UnitName,
                            m.ConsumingUnits))
                        .ToList(),
                });
        }
        catch (UnknownCredentialEdgeException ex)
        {
            // #2159: a credential was supplied for an edge no member unit
            // consumes. Structurally invalid; surface as 400.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/unknown-credential-edge",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "UnknownCredentialEdge",
                    ["provider"] = ex.Provider,
                    ["authMethod"] = FormatAuthMethod(ex.AuthMethod),
                });
        }
        catch (InvalidInstallScopeException ex)
        {
            // ADR-0043 §6: `--into` reference failed validation. Surface as
            // a 400 with a precise code so the CLI can render the message.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/invalid-install-scope",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidInstallScope",
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
        catch (AmbiguousDisplayNameException ex)
        {
            // #2310: display-name override is singular — multi-top-level
            // packages reject the override with a precise 400 so the wizard
            // / CLI can surface the rejection without parsing prose.
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://cvoya.com/problems/ambiguous-display-name",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "AmbiguousDisplayName",
                    ["packageName"] = ex.PackageName,
                    ["topLevelCount"] = ex.TopLevelCount,
                });
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
            // #2159: project the wire credential payload into the
            // service-layer CredentialBinding shape. Reject malformed
            // authMethod values up-front so the install service stays
            // strict-typed.
            var credentials = ProjectCredentials(t.Credentials);

            result.Add(new InstallTarget(
                PackageName: t.PackageName,
                Inputs: t.Inputs ?? new Dictionary<string, string>(),
                OriginalYaml: yaml,
                PackageRoot: packageRoot,
                PackageBindings: pkgBindings,
                UnitBindings: unitBindings,
                Credentials: credentials,
                IntoUnit: t.IntoUnit,
                DisplayName: t.DisplayName));
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
    /// #2159: project the wire credential payload into the
    /// service-layer <see cref="Cvoya.Spring.Manifest.CredentialBinding"/>
    /// shape. Throws a <see cref="System.ArgumentException"/> on
    /// unrecognised <c>authMethod</c> values so the endpoint surfaces
    /// a clean 400 rather than the resolver complaining about an
    /// unmatched edge.
    /// </summary>
    private static IReadOnlyList<Cvoya.Spring.Manifest.CredentialBinding>? ProjectCredentials(
        IReadOnlyList<CredentialBindingPayload>? payloads)
    {
        if (payloads is null || payloads.Count == 0)
        {
            return null;
        }
        var result = new List<Cvoya.Spring.Manifest.CredentialBinding>(payloads.Count);
        foreach (var p in payloads)
        {
            if (string.IsNullOrWhiteSpace(p.Provider))
            {
                throw new System.ArgumentException("Credential provider is required.");
            }
            if (string.IsNullOrWhiteSpace(p.Value))
            {
                throw new System.ArgumentException(
                    $"Credential value for provider '{p.Provider}' is empty.");
            }
            result.Add(new Cvoya.Spring.Manifest.CredentialBinding(
                Provider: p.Provider.Trim().ToLowerInvariant(),
                AuthMethod: ParseAuthMethod(p.AuthMethod),
                Value: p.Value));
        }
        return result;
    }

    private static Cvoya.Spring.Core.Catalog.AuthMethod ParseAuthMethod(string raw) =>
        (raw ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "oauth" => Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
            "api-key" => Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey,
            _ => throw new System.ArgumentException(
                $"authMethod must be 'oauth' or 'api-key' (got '{raw}')."),
        };

    private static string FormatAuthMethod(Cvoya.Spring.Core.Catalog.AuthMethod method) =>
        method switch
        {
            Cvoya.Spring.Core.Catalog.AuthMethod.Oauth => "oauth",
            Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey => "api-key",
            _ => method.ToString().ToLowerInvariant(),
        };

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
                p.ErrorMessage,
                p.CreatedUnitNames,
                p.CreatedAgentIds))
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
