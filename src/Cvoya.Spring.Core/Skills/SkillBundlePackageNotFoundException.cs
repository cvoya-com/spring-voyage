// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core;

/// <summary>
/// Thrown by <see cref="ISkillBundleResolver.ResolveAsync"/> when the package
/// named in a <see cref="SkillBundleReference"/> cannot be located. The
/// <see cref="SearchPath"/> property exposes the search hint (e.g., the
/// on-disk directory the file-system resolver looked in) so operators see
/// exactly where the resolver expected to find the package.
/// </summary>
public class SkillBundlePackageNotFoundException : SpringException
{
    /// <summary>
    /// Creates a new <see cref="SkillBundlePackageNotFoundException"/>.
    /// </summary>
    public SkillBundlePackageNotFoundException(string packageName, string searchPath)
        : base($"Skill package '{packageName}' was not found. Searched: {searchPath}")
    {
        PackageName = packageName;
        SearchPath = searchPath;
    }

    /// <summary>The package name that could not be resolved.</summary>
    public string PackageName { get; }

    /// <summary>Diagnostic hint — the location the resolver searched.</summary>
    public string SearchPath { get; }
}