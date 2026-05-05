// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Manifest;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Pure connector-binding resolver (#1671). Computes the per-unit
/// connector bindings for a single package install given:
/// <list type="bullet">
///   <item><description>The package-level <c>connectors:</c> declarations.</description></item>
///   <item><description>Each member unit's own <c>connectors:</c> block.</description></item>
///   <item><description>The package-scope bindings supplied by the operator.</description></item>
///   <item><description>The per-unit binding overrides supplied by the operator.</description></item>
/// </list>
/// Pure: no I/O, no DB access. Output is <c>unit name → slug → binding</c>;
/// the install pipeline forwards each unit's map to
/// <see cref="IUnitCreationService.CreateFromManifestAsync"/> via the
/// activator. Pre-flight gaps are surfaced as
/// <see cref="ConnectorBindingMissing"/> entries so the caller can return
/// a single 400 with every missing slug at once instead of dripping out
/// errors one Phase-2 activation at a time.
/// </summary>
public static class ConnectorBindingResolver
{
    /// <summary>
    /// Resolves the per-unit connector bindings. Returns a tuple:
    /// <list type="bullet">
    ///   <item><description><c>Bindings</c>: unit name → slug → binding.</description></item>
    ///   <item><description><c>Missing</c>: gaps the operator must fix.</description></item>
    ///   <item><description><c>UnknownSlugs</c>: bindings supplied for slugs the package does not declare.</description></item>
    /// </list>
    /// </summary>
    public static ConnectorBindingResolution Resolve(
        ResolvedPackage package,
        IReadOnlyDictionary<string, ConnectorBinding>? packageBindings,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>? unitBindings)
    {
        ArgumentNullException.ThrowIfNull(package);

        packageBindings ??= new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
        unitBindings ??= new Dictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>(StringComparer.OrdinalIgnoreCase);

        var declared = package.Connectors ?? Array.Empty<RequiredConnector>();
        var declaredSlugs = new HashSet<string>(
            declared.Where(c => !string.IsNullOrWhiteSpace(c.Type)).Select(c => c.Type!),
            StringComparer.OrdinalIgnoreCase);

        var unitNames = package.Units
            .Where(u => !u.IsCrossPackage)
            .Select(u => u.Name)
            .ToList();

        var missing = new List<ConnectorBindingMissing>();
        var unknown = new List<UnknownConnectorBindingEntry>();

        // 1. Validate that every supplied slug is declared by the package.
        foreach (var slug in packageBindings.Keys)
        {
            if (!declaredSlugs.Contains(slug))
            {
                unknown.Add(new UnknownConnectorBindingEntry(slug, "package", null));
            }
        }
        foreach (var (unitName, perUnit) in unitBindings)
        {
            foreach (var slug in perUnit.Keys)
            {
                if (!declaredSlugs.Contains(slug))
                {
                    unknown.Add(new UnknownConnectorBindingEntry(slug, "unit", unitName));
                }
            }
        }

        // 2. Required-but-not-supplied at the package level.
        foreach (var conn in declared)
        {
            if (!conn.Required) continue;
            if (string.IsNullOrWhiteSpace(conn.Type)) continue;
            if (!packageBindings.ContainsKey(conn.Type!))
            {
                missing.Add(new ConnectorBindingMissing(conn.Type!, "package", null));
            }
        }

        // 3. Walk each member unit, computing its inherited bindings then
        //    overlaying explicit unit-scope overrides. Per-unit `inherit:
        //    false` opt-out without a unit-scope override is a hard error.
        var perUnitBindings = new Dictionary<string, IReadOnlyDictionary<string, ConnectorBinding>>(StringComparer.OrdinalIgnoreCase);

        // Parse each unit's `connectors:` block once so we can read
        // `inherit: false` opt-out flags. Lives here in the resolver so the
        // function stays pure (no shared state with the install pipeline).
        var unitOptOuts = ParseUnitOptOuts(package);

        foreach (var unitName in unitNames)
        {
            var combined = new Dictionary<string, ConnectorBinding>(StringComparer.OrdinalIgnoreCase);
            unitOptOuts.TryGetValue(unitName, out var optOutSlugs);

            foreach (var conn in declared)
            {
                if (string.IsNullOrWhiteSpace(conn.Type)) continue;
                var slug = conn.Type!;

                // Determine inheritance scope.
                var inherits =
                    conn.InheritAll
                    || (conn.InheritUnits is not null
                        && conn.InheritUnits.Any(n => string.Equals(n, unitName, StringComparison.OrdinalIgnoreCase)));

                // Per-unit opt-out turns inheritance off.
                if (optOutSlugs is not null && optOutSlugs.Contains(slug))
                {
                    inherits = false;
                }

                if (inherits && packageBindings.TryGetValue(slug, out var inherited))
                {
                    combined[slug] = inherited;
                }
            }

            // Overlay unit-scope explicit overrides.
            if (unitBindings.TryGetValue(unitName, out var perUnit))
            {
                foreach (var (slug, binding) in perUnit)
                {
                    combined[slug] = binding;
                }
            }

            // Hard pre-flight error: unit opt-outs that have no override.
            if (optOutSlugs is not null)
            {
                foreach (var slug in optOutSlugs)
                {
                    if (!combined.ContainsKey(slug)
                        && !(unitBindings.TryGetValue(unitName, out var perU) && perU.ContainsKey(slug)))
                    {
                        missing.Add(new ConnectorBindingMissing(slug, "unit", unitName));
                    }
                }
            }

            perUnitBindings[unitName] = combined;
        }

        return new ConnectorBindingResolution(perUnitBindings, missing, unknown);
    }

    private static Dictionary<string, HashSet<string>> ParseUnitOptOuts(ResolvedPackage package)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in package.Units.Where(u => !u.IsCrossPackage && u.Content is not null))
        {
            UnitManifest? manifest;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var doc = deserializer.Deserialize<ManifestDocument>(u.Content!);
                manifest = doc?.Unit;
            }
            catch
            {
                continue;
            }

            if (manifest?.Connectors is not { Count: > 0 } unitConnectors)
            {
                continue;
            }

            var optOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in unitConnectors)
            {
                if (string.IsNullOrWhiteSpace(c?.Type)) continue;
                if (!c!.Inherit)
                {
                    optOut.Add(c.Type!);
                }
            }
            if (optOut.Count > 0)
            {
                result[u.Name] = optOut;
            }
        }
        return result;
    }
}

/// <summary>
/// Output of <see cref="ConnectorBindingResolver.Resolve"/>.
/// </summary>
/// <param name="Bindings">Per-unit resolved bindings (<c>unit → slug → binding</c>).</param>
/// <param name="Missing">Pre-flight gaps the install request must fix before any DB writes.</param>
/// <param name="UnknownSlugs">Bindings supplied for slugs the package does not declare.</param>
public sealed record ConnectorBindingResolution(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBinding>> Bindings,
    IReadOnlyList<ConnectorBindingMissing> Missing,
    IReadOnlyList<UnknownConnectorBindingEntry> UnknownSlugs);

/// <summary>
/// One entry in <see cref="ConnectorBindingResolution.UnknownSlugs"/> —
/// a binding supplied for a slug the package does not declare.
/// </summary>
public sealed record UnknownConnectorBindingEntry(string Slug, string Scope, string? UnitName);