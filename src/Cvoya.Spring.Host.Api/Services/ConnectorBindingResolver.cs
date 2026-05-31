// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Manifest;

/// <summary>
/// Pure connector-binding resolver. Computes per-artefact connector
/// bindings for a single package install given:
/// <list type="bullet">
///   <item><description>The package's declared requires (union of artefact <c>requires:</c> blocks; ADR-0037 D3).</description></item>
///   <item><description>The package-scope bindings supplied by the operator (one per slug).</description></item>
///   <item><description>Optional per-unit binding overrides supplied by the operator.</description></item>
/// </list>
/// Pure: no I/O, no DB access. Output is <c>unit name → slug → binding</c>;
/// the install pipeline forwards each unit's map to
/// <see cref="IUnitCreationService.CreateFromManifestAsync"/> via the
/// activator. Pre-flight gaps are surfaced as
/// <see cref="ConnectorBindingMissing"/> entries so the caller can return
/// a single 400 with every missing slug at once instead of dripping out
/// errors one Phase-2 activation at a time.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0037 D3 retired the package-level <c>connectors:</c> block and the
/// <c>inherit:</c> matrix it used. Each artefact declares its own
/// <c>requires:</c>; the resolver applies the operator's
/// per-slug binding to every artefact that declared it. Per-unit
/// overrides still flow through <c>unitBindings</c> for the rare case
/// where one unit needs a different binding than its peers.
/// </para>
/// </remarks>
public static class ConnectorBindingResolver
{
    /// <summary>
    /// Resolves the per-unit connector bindings.
    /// </summary>
    /// <param name="package">The resolved package being installed.</param>
    /// <param name="packageBindings">Operator-supplied package-scope bindings, keyed by slug.</param>
    /// <param name="unitBindings">Operator-supplied per-unit override bindings.</param>
    /// <param name="knownConnectorSlugs">
    /// The slugs of every connector type registered on this deployment. A
    /// supplied binding whose slug the package does not declare is an
    /// operator-added <em>extra</em> connector — perfectly legal: an
    /// operator may bind a connector a package author did not require (e.g.
    /// adding GitHub to <c>hello-world</c>). It is applied to the package's
    /// top-level unit(s). Only a slug that matches <em>no registered
    /// connector type</em> is rejected as an <see cref="UnknownConnectorBindingEntry"/>.
    /// When <see langword="null"/> the resolver cannot tell a real connector
    /// from a typo, so it accepts every supplied slug (the install pipeline
    /// always passes the live set; this null path is for callers/tests that
    /// don't care about typo rejection).
    /// </param>
    public static ConnectorBindingResolution Resolve(
        ResolvedPackage package,
        IReadOnlyDictionary<string, ConnectorBinding>? packageBindings,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>? unitBindings,
        IReadOnlySet<string>? knownConnectorSlugs = null)
    {
        ArgumentNullException.ThrowIfNull(package);

        packageBindings ??= new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
        unitBindings ??= new Dictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>(StringComparer.OrdinalIgnoreCase);

        var declaredSlugs = new HashSet<string>(
            package.RequiredConnectorSlugs ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var missing = new List<ConnectorBindingMissing>();
        var unknown = new List<UnknownConnectorBindingEntry>();

        // A supplied slug is only "unknown" when it matches no registered
        // connector type. A registered-but-undeclared slug is an operator-
        // added extra (allowed) — see `knownConnectorSlugs`. With no known
        // set, nothing is rejected here (the caller opted out of the check).
        bool IsUnknown(string slug) =>
            !declaredSlugs.Contains(slug)
            && knownConnectorSlugs is not null
            && !knownConnectorSlugs.Contains(slug);

        // 1. Reject only genuinely-unknown connector types (typos / connectors
        //    not installed on this deployment). Undeclared-but-registered
        //    slugs fall through and are applied as extras below.
        foreach (var slug in packageBindings.Keys)
        {
            if (IsUnknown(slug))
            {
                unknown.Add(new UnknownConnectorBindingEntry(slug, "package", null));
            }
        }
        foreach (var (unitName, perUnit) in unitBindings)
        {
            foreach (var slug in perUnit.Keys)
            {
                if (IsUnknown(slug))
                {
                    unknown.Add(new UnknownConnectorBindingEntry(slug, "unit", unitName));
                }
            }
        }

        // 2. Required-but-not-supplied at the package level. Under ADR-0037 D3
        //    every declared requirement is required (no optional flag); if the
        //    operator hasn't supplied a binding for a slug some artefact
        //    declared, that's a pre-flight error.
        foreach (var slug in declaredSlugs)
        {
            if (!packageBindings.ContainsKey(slug))
            {
                missing.Add(new ConnectorBindingMissing(slug, "package", null));
            }
        }

        // Operator-added extras: package-scope bindings for slugs no artefact
        // declared. They have no `requires:` edge to attach to, so they bind
        // to the package's top-level unit(s) and member agents inherit the
        // credential through the parent-chain binding-walk (#2380). Skipped
        // when the slug was rejected as unknown above.
        var extraPackageSlugs = packageBindings.Keys
            .Where(s => !declaredSlugs.Contains(s) && !IsUnknown(s))
            .ToList();
        var topLevelUnitNames = new HashSet<string>(
            package.Units.Where(u => u.IsTopLevel).Select(u => u.Name),
            StringComparer.OrdinalIgnoreCase);

        // 3. Walk each member unit. Apply the package-scope binding for
        //    every slug the unit declared in its `requires:` block (per
        //    ADR-0037 D3); attach operator-added extras to top-level units;
        //    overlay any explicit unit-scope override.
        var unitNames = package.Units
            .Where(u => !u.IsCrossPackage)
            .Select(u => u.Name)
            .ToList();

        var perUnitBindings = new Dictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var unitName in unitNames)
        {
            var combined = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase);

            // Apply each slug declared by this unit.
            if (package.ConnectorRequiresByArtefact.TryGetValue(unitName, out var unitSlugs))
            {
                foreach (var slug in unitSlugs)
                {
                    if (packageBindings.TryGetValue(slug, out var binding))
                    {
                        combined[slug] = binding;
                    }
                }
            }

            // Attach operator-added extras to the package's top-level units.
            if (topLevelUnitNames.Contains(unitName))
            {
                foreach (var slug in extraPackageSlugs)
                {
                    combined[slug] = packageBindings[slug];
                }
            }

            // Overlay explicit unit-scope overrides.
            if (unitBindings.TryGetValue(unitName, out var perUnit))
            {
                foreach (var (slug, binding) in perUnit)
                {
                    combined[slug] = binding;
                }
            }

            perUnitBindings[unitName] = combined;
        }

        return new ConnectorBindingResolution(perUnitBindings, missing, unknown);
    }
}

/// <summary>
/// Output of <see cref="ConnectorBindingResolver.Resolve"/>.
/// </summary>
public sealed record ConnectorBindingResolution(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>> Bindings,
    IReadOnlyList<ConnectorBindingMissing> Missing,
    IReadOnlyList<UnknownConnectorBindingEntry> UnknownSlugs);

/// <summary>
/// One entry in <see cref="ConnectorBindingResolution.UnknownSlugs"/> —
/// a binding supplied for a slug the package does not declare.
/// </summary>
public sealed record UnknownConnectorBindingEntry(string Slug, string Scope, string? UnitName);
