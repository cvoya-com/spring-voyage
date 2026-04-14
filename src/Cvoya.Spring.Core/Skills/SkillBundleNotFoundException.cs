// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core;

/// <summary>
/// Thrown by <see cref="ISkillBundleResolver.ResolveAsync"/> when the package
/// is known but the named skill file does not exist inside it.
/// </summary>
public class SkillBundleNotFoundException : SpringException
{
    /// <summary>
    /// Creates a new <see cref="SkillBundleNotFoundException"/>.
    /// </summary>
    public SkillBundleNotFoundException(string packageName, string skillName, string searchPath)
        : base($"Skill '{skillName}' was not found in package '{packageName}'. Searched: {searchPath}")
    {
        PackageName = packageName;
        SkillName = skillName;
        SearchPath = searchPath;
    }

    /// <summary>The package the resolver searched.</summary>
    public string PackageName { get; }

    /// <summary>The skill name that could not be resolved.</summary>
    public string SkillName { get; }

    /// <summary>Diagnostic hint — the location the resolver expected the skill to live at.</summary>
    public string SearchPath { get; }
}