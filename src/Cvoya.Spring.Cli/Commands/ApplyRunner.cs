// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Manifest;

/// <summary>
/// Parses a unit manifest YAML file and applies it against a <see cref="SpringApiClient"/>.
/// Thin wrapper over <see cref="ManifestParser"/> so the parse + apply logic can be unit
/// tested without going through <c>System.CommandLine</c>. The parser itself now lives
/// in <c>Cvoya.Spring.Manifest</c> so the API host can reuse it from the
/// <c>/units/from-yaml</c> and <c>/units/from-template</c> endpoints.
/// </summary>
public static class ApplyRunner
{
    /// <summary>
    /// Sections of the unit manifest grammar that are parsed but not yet wired up
    /// through <see cref="SpringApiClient"/>. Exposed via the shared parser so the
    /// CLI and API emit the same warning vocabulary.
    /// </summary>
    internal static IReadOnlyList<string> UnsupportedSections => ManifestParser.UnsupportedSections;

    /// <summary>
    /// Parses the manifest YAML text. Delegates to <see cref="ManifestParser.Parse"/>.
    /// </summary>
    public static UnitManifest Parse(string yamlText) => ManifestParser.Parse(yamlText);

    /// <summary>
    /// Parses the manifest at <paramref name="filePath"/>. Delegates to
    /// <see cref="ManifestParser.ParseFile"/>.
    /// </summary>
    public static UnitManifest ParseFile(string filePath) => ManifestParser.ParseFile(filePath);

    /// <summary>
    /// Applies a parsed manifest through <paramref name="client"/>.
    /// Writes progress to <paramref name="stdout"/> and errors to <paramref name="stderr"/>.
    /// Returns a non-zero exit code if the API rejects any call.
    /// </summary>
    public static async Task<int> ApplyAsync(
        UnitManifest manifest,
        SpringApiClient client,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct = default)
    {
        var unitName = manifest.Name!;

        stdout.WriteLine($"[apply] creating unit '{unitName}'...");
        try
        {
            // Forward description from the manifest; display name defaults to the unit name
            // when no separate display name is declared in the manifest today.
            await client.CreateUnitAsync(unitName, unitName, manifest.Description, ct: ct);
        }
        catch (System.Exception ex)
        {
            stderr.WriteLine($"[error] failed to create unit '{unitName}': {ex.Message}");
            return 1;
        }

        var createdMembers = 0;
        foreach (var member in manifest.Members ?? new List<MemberManifest>())
        {
            var address = ResolveMemberAddress(member);
            if (address is null)
            {
                stdout.WriteLine("[warn] member entry has no 'agent' or 'unit' field; skipping");
                continue;
            }

            try
            {
                await client.AddMemberAsync(unitName, address.Value.Scheme, address.Value.Path, ct);
                stdout.WriteLine($"[apply] added member {address.Value.Scheme}:{address.Value.Path}");
                createdMembers++;
            }
            catch (System.Exception ex)
            {
                stderr.WriteLine(
                    $"[error] failed to add member {address.Value.Scheme}:{address.Value.Path} to unit '{unitName}': {ex.Message}");
                return 1;
            }
        }

        // #494: if the manifest declared a non-empty boundary, PUT it now so
        // the unit actor ends up with the same state a subsequent
        // `spring unit boundary set` would produce. We call the unified
        // `/api/v1/units/{id}/boundary` endpoint — the same one the CLI's
        // boundary verbs hit — so YAML-applied and CLI-applied boundaries
        // are wire-identical.
        if (manifest.Boundary is { IsEmpty: false })
        {
            var body = ProjectBoundaryToResponse(manifest.Boundary);
            try
            {
                await client.SetUnitBoundaryAsync(unitName, body, ct);
                stdout.WriteLine($"[apply] applied boundary rules for unit '{unitName}'.");
            }
            catch (System.Exception ex)
            {
                stderr.WriteLine(
                    $"[error] failed to apply boundary for unit '{unitName}': {ex.Message}");
                return 1;
            }
        }

        WarnUnsupportedSections(manifest, stdout);

        stdout.WriteLine($"[apply] done: unit '{unitName}', {createdMembers} member(s) added.");
        return 0;
    }

    /// <summary>
    /// Prints a dry-run plan for the manifest without invoking any API.
    /// </summary>
    public static void PrintPlan(UnitManifest manifest, TextWriter stdout)
    {
        var unitName = manifest.Name!;
        stdout.WriteLine($"[dry-run] plan for unit '{unitName}':");
        stdout.WriteLine($"[dry-run]   create unit '{unitName}'");

        if (manifest.Members is { Count: > 0 })
        {
            foreach (var member in manifest.Members)
            {
                var address = ResolveMemberAddress(member);
                if (address is null)
                {
                    stdout.WriteLine("[dry-run]   (skipped member with no 'agent' or 'unit' field)");
                    continue;
                }
                stdout.WriteLine($"[dry-run]   add member {address.Value.Scheme}:{address.Value.Path}");
            }
        }
        else
        {
            stdout.WriteLine("[dry-run]   (no members declared)");
        }

        if (manifest.Boundary is { IsEmpty: false } boundary)
        {
            var opacityCount = boundary.Opacities?.Count ?? 0;
            var projectionCount = boundary.Projections?.Count ?? 0;
            var synthesisCount = boundary.Syntheses?.Count ?? 0;
            stdout.WriteLine(
                $"[dry-run]   apply boundary (opacities: {opacityCount}, projections: {projectionCount}, syntheses: {synthesisCount})");
        }

        WarnUnsupportedSections(manifest, stdout);
        stdout.WriteLine("[dry-run] no API calls were made.");
    }

    /// <summary>
    /// Projects a manifest-layer <see cref="BoundaryManifest"/> onto the
    /// Kiota-generated <see cref="UnitBoundaryResponse"/> accepted by
    /// <see cref="SpringApiClient.SetUnitBoundaryAsync"/>. Kept here rather
    /// than in <c>Cvoya.Spring.Manifest</c> so the manifest library stays
    /// free of a client dependency, and kept parallel to
    /// <c>ManifestBoundaryMapper</c> on the server side so both code paths
    /// apply the same tolerance rules (blank synthesis names dropped,
    /// nothing fabricated for malformed entries).
    /// </summary>
    internal static UnitBoundaryResponse ProjectBoundaryToResponse(BoundaryManifest boundary)
    {
        System.ArgumentNullException.ThrowIfNull(boundary);

        var opacities = boundary.Opacities?
            .Where(r => r is not null)
            .Select(r => new BoundaryOpacityRuleDto
            {
                DomainPattern = r!.DomainPattern,
                OriginPattern = r.OriginPattern,
            })
            .ToList();

        var projections = boundary.Projections?
            .Where(r => r is not null)
            .Select(r => new BoundaryProjectionRuleDto
            {
                DomainPattern = r!.DomainPattern,
                OriginPattern = r.OriginPattern,
                RenameTo = r.RenameTo,
                Retag = r.Retag,
                OverrideLevel = r.OverrideLevel,
            })
            .ToList();

        var syntheses = boundary.Syntheses?
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r!.Name))
            .Select(r => new BoundarySynthesisRuleDto
            {
                Name = r!.Name!,
                DomainPattern = r.DomainPattern,
                OriginPattern = r.OriginPattern,
                Description = r.Description,
                Level = r.Level,
            })
            .ToList();

        return new UnitBoundaryResponse
        {
            Opacities = opacities is { Count: > 0 } ? opacities : null,
            Projections = projections is { Count: > 0 } ? projections : null,
            Syntheses = syntheses is { Count: > 0 } ? syntheses : null,
        };
    }

    private static (string Scheme, string Path)? ResolveMemberAddress(MemberManifest member)
    {
        if (!string.IsNullOrWhiteSpace(member.Agent))
        {
            return ("agent", member.Agent!);
        }
        if (!string.IsNullOrWhiteSpace(member.Unit))
        {
            return ("unit", member.Unit!);
        }
        return null;
    }

    private static void WarnUnsupportedSections(UnitManifest manifest, TextWriter stdout)
    {
        foreach (var section in ManifestParser.CollectUnsupportedSections(manifest))
        {
            stdout.WriteLine(
                $"[warn] section '{section}' is parsed but not yet applied (follow-up issue pending)");
        }
    }
}