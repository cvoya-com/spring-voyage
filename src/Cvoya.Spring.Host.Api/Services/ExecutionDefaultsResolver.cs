// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Manifest;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Pure execution-defaults resolver (#1679). Computes the merged
/// per-unit execution block for a single package install given the
/// resolved package's <see cref="ResolvedPackage.Execution"/>
/// declaration and each member unit's parsed
/// <see cref="UnitManifest.Execution"/>. Symmetric to
/// <see cref="ConnectorBindingResolver"/> in spirit (pure, no I/O,
/// pre-flight gaps surfaced as a structured error list) but the merge
/// semantics are different — connectors apply 1:1 to artefacts that
/// declared them, while execution defaults are field-wise inherited
/// with member-level overrides.
/// </summary>
/// <remarks>
/// <para>
/// Field merge: for each member unit eligible to inherit
/// (per <see cref="PackageExecutionDeclaration.AppliesTo"/>), the
/// resolver returns a per-field projection where the member's
/// non-null fields win and the package's non-null fields fill the
/// gaps. A member that declared no <c>execution:</c> at all picks up
/// the package's full block; a member that declared only
/// <c>execution.image</c> keeps its own image and inherits the
/// package's runtime / provider / model.
/// </para>
/// <para>
/// Pre-flight gap: <c>execution.image</c> is required for every unit
/// at activation time (the validator already enforces this against
/// the persisted state in
/// <c>UnitValidationWorkflowScheduler</c>). The resolver fails fast
/// with <see cref="ExecutionConfigurationMissing"/> entries when
/// neither side declares an image for an inheriting member, so the
/// install endpoint can return a single 400 listing every offending
/// unit instead of dripping out activation failures one Phase-2
/// dispatch at a time.
/// </para>
/// </remarks>
public static class ExecutionDefaultsResolver
{
    /// <summary>
    /// Resolves the per-unit merged execution defaults.
    /// </summary>
    /// <param name="package">The resolved package.</param>
    /// <returns>
    /// A <see cref="ExecutionDefaultsResolution"/> carrying the merged
    /// per-unit defaults and any pre-flight gaps.
    /// </returns>
    public static ExecutionDefaultsResolution Resolve(ResolvedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var packageExec = package.Execution;
        var perUnit = new Dictionary<string, ResolvedExecutionDefaults>(
            StringComparer.OrdinalIgnoreCase);
        var missing = new List<ExecutionConfigurationMissing>();

        foreach (var unit in package.Units)
        {
            if (unit.IsCrossPackage)
            {
                continue;
            }

            // Read the member's own execution block from its parsed
            // YAML. Cheap (one YamlDotNet deserialise per unit) and
            // localised so the resolver stays a pure function — the
            // alternative (passing in a pre-parsed map) would push the
            // YAML parsing out of the install service into every
            // caller of the resolver.
            var memberExec = ParseMemberExecution(unit.Content);
            var inherits = packageExec is not null && packageExec.AppliesTo(unit.Name);

            string? image, provider, model;
            if (inherits && packageExec is not null)
            {
                // Field-wise merge: member non-null beats package; null
                // member fields fall through to the package's values.
                // ADR-0038: ExecutionManifest no longer carries `provider` —
                // it's intrinsic to ai.model.provider. The package-level
                // declaration still surfaces it during the inheritance
                // transition; the unit-level write reads only image/model.
                image = FirstNonBlank(memberExec?.Image, packageExec.Image);
                provider = packageExec.Provider;
                model = FirstNonBlank(memberExec?.Model, packageExec.Model);
            }
            else
            {
                // Member opted out of inheritance (not in
                // `inherit: [list]`) or no package-level block was
                // declared. The member's own block is the only source.
                image = NullIfBlank(memberExec?.Image);
                provider = null;
                model = NullIfBlank(memberExec?.Model);
            }

            // Pre-flight gap: `image` is the only field the validator
            // hard-requires for v0.1. Other fields are optional — the
            // platform falls back to runtime defaults when the unit
            // doesn't declare them.
            if (string.IsNullOrWhiteSpace(image))
            {
                missing.Add(new ExecutionConfigurationMissing(unit.Name, "image"));
            }

            perUnit[unit.Name] = new ResolvedExecutionDefaults(image, provider, model);
        }

        return new ExecutionDefaultsResolution(perUnit, missing);
    }

    private static ExecutionManifest? ParseMemberExecution(string? unitYaml)
    {
        if (string.IsNullOrWhiteSpace(unitYaml))
        {
            return null;
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .IgnoreUnmatchedProperties()
                .Build();
            var manifest = deserializer.Deserialize<UnitManifest>(unitYaml);
            return manifest?.Execution;
        }
        catch
        {
            // The full ManifestParser path will surface the same parse
            // failure later in the install pipeline; the resolver
            // returns a null member block here so the package-level
            // defaults still apply unobstructed.
            return null;
        }
    }

    private static string? FirstNonBlank(string? a, string? b)
        => string.IsNullOrWhiteSpace(a) ? NullIfBlank(b) : a;

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// Output of <see cref="ExecutionDefaultsResolver.Resolve"/>.
/// </summary>
/// <param name="ByUnit">
/// Merged per-unit execution defaults, keyed by member unit name.
/// Empty when the package declares no member units.
/// </param>
/// <param name="Missing">
/// Pre-flight gaps — one entry per member unit whose merged
/// <c>image</c> is unresolvable. The install endpoint surfaces the
/// list as a structured 400 with <c>code = "ConfigurationIncomplete"</c>.
/// </param>
public sealed record ExecutionDefaultsResolution(
    IReadOnlyDictionary<string, ResolvedExecutionDefaults> ByUnit,
    IReadOnlyList<ExecutionConfigurationMissing> Missing);

/// <summary>
/// Merged execution defaults for one member unit (#1679). Each field
/// reflects the field-wise merge of the package's
/// <see cref="PackageExecutionDeclaration"/> and the unit's own
/// <see cref="ExecutionManifest"/>: member non-null fields win;
/// member-null fields fall through to the package's values when the
/// unit is eligible to inherit.
/// </summary>
/// <param name="Image">Resolved container image, or null when neither side declared one.</param>
/// <param name="Provider">Resolved LLM provider, or null.</param>
/// <param name="Model">Resolved model identifier, or null.</param>
public sealed record ResolvedExecutionDefaults(
    string? Image,
    string? Provider,
    string? Model)
{
    /// <summary>True when every field is null.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Provider)
        && string.IsNullOrWhiteSpace(Model);
}
