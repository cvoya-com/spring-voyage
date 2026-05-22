// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

/// <summary>
/// Resolved package-level <c>execution:</c> declaration (#1679). Built by
/// <see cref="PackageManifestParser"/> from the raw
/// <see cref="PackageExecutionManifest"/> and surfaced on
/// <see cref="ResolvedPackage"/> so the install pipeline (and the
/// <c>GET /api/v1/packages/{name}</c> wire shape) can consume the same
/// shape parsers, resolvers, and tests do.
/// </summary>
/// <remarks>
/// <para>
/// Per the ADR-0038 amendment (#2634) the package-level
/// <c>execution:</c> block carries only the container
/// <see cref="Image"/>. Member units pick it up unless they override;
/// the merge happens in <c>ExecutionDefaultsResolver.Resolve</c> at
/// install time.
/// </para>
/// <para>
/// <see cref="InheritUnits"/> is the discriminated form of the package
/// manifest's <c>inherit:</c> child key: <c>null</c> means every
/// member inherits (the default and the <c>inherit: all</c> spelling);
/// a non-null list restricts inheritance to the named members.
/// </para>
/// </remarks>
/// <param name="Image">Default container image.</param>
/// <param name="InheritUnits">
/// <c>null</c> when every member inherits; otherwise the explicit list
/// of unit names that participate.
/// </param>
public sealed record PackageExecutionDeclaration(
    string? Image,
    IReadOnlyList<string>? InheritUnits)
{
    /// <summary>True when every inheritable field is null / whitespace.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image);

    /// <summary>
    /// True when <paramref name="unitName"/> is eligible to inherit the
    /// package-level defaults. <see cref="InheritUnits"/> is
    /// <c>null</c> ⇒ every member inherits; otherwise membership of
    /// the explicit list (case-insensitive) decides.
    /// </summary>
    public bool AppliesTo(string unitName)
    {
        if (InheritUnits is null)
        {
            return true;
        }

        foreach (var name in InheritUnits)
        {
            if (string.Equals(name, unitName, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Pre-flight gap in the install request: a member unit eligible to
/// inherit the package's <c>execution:</c> defaults cannot be
/// activated because neither the package nor the member declared a
/// container <c>image</c>. Surfaced through
/// <see cref="ExecutionConfigurationsMissingException"/> as a
/// structured 400 so the wizard / CLI can render a precise per-unit
/// error rather than parse the prose detail string. Mirrors the
/// shape of <see cref="ConnectorBindingMissing"/> from #1671 — same
/// payload model, same operator-actionable structure.
/// </summary>
/// <param name="UnitName">The member unit that has no resolvable image.</param>
/// <param name="Field">
/// The execution field that is unresolvable (<c>"image"</c> for v0.1).
/// Carried on the wire shape so future required fields can extend the
/// payload without breaking existing callers.
/// </param>
public sealed record ExecutionConfigurationMissing(
    string UnitName,
    string Field);

/// <summary>
/// Thrown by the install pipeline when one or more member units would
/// activate without a resolvable <c>execution.image</c> after the
/// package-level → member-level merge (#1679). The endpoint maps this
/// to a 400 with <c>code = "ConfigurationIncomplete"</c> and the list
/// of missing units in <c>extensions["missing"]</c>. No DB writes
/// occur — the pre-flight runs entirely before Phase 1.
/// </summary>
public sealed class ExecutionConfigurationsMissingException : System.Exception
{
    /// <summary>Initialises a new <see cref="ExecutionConfigurationsMissingException"/>.</summary>
    public ExecutionConfigurationsMissingException(IReadOnlyList<ExecutionConfigurationMissing> missing)
        : base(BuildMessage(missing))
    {
        Missing = missing;
    }

    /// <summary>The structured list of units missing an executable image.</summary>
    public IReadOnlyList<ExecutionConfigurationMissing> Missing { get; }

    private static string BuildMessage(IReadOnlyList<ExecutionConfigurationMissing> missing)
    {
        var parts = new List<string>(missing.Count);
        foreach (var m in missing)
        {
            parts.Add(
                $"declare execution.{m.Field} at the package or member level for unit '{m.UnitName}'");
        }
        return "ConfigurationIncomplete: " + string.Join("; ", parts);
    }
}
