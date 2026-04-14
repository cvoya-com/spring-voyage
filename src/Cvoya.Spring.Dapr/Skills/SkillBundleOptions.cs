// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Generic;

/// <summary>
/// Options for the file-system backed <see cref="FileSystemSkillBundleResolver"/>.
/// </summary>
public class SkillBundleOptions
{
    /// <summary>Configuration section: <c>Skills</c>.</summary>
    public const string SectionName = "Skills";

    /// <summary>
    /// Absolute or relative path to the <c>packages/</c> root that contains
    /// <c>{package}/skills/{skill}.md</c> files. When <c>null</c> or missing,
    /// the resolver throws <see cref="Core.Skills.SkillBundlePackageNotFoundException"/>
    /// for every request — the operator sees the misconfiguration instead of a
    /// silent fallback.
    /// </summary>
    public string? PackagesRoot { get; set; }

    /// <summary>
    /// Optional namespace prefixes that may appear in a
    /// <c>SkillBundleReference.Package</c> value (e.g. <c>spring-voyage/</c>).
    /// When a package name starts with one of these prefixes the resolver
    /// strips the prefix and looks up the remaining segment on disk.
    /// Defaults to <c>{ "spring-voyage/" }</c> so manifests can continue to
    /// write the canonical <c>spring-voyage/software-engineering</c> form.
    /// </summary>
    public IList<string> NamespacePrefixes { get; } = new List<string> { "spring-voyage/" };
}