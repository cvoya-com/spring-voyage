// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// A resolved package-level skill bundle. Produced by
/// <see cref="ISkillBundleResolver.ResolveAsync"/> from an on-disk (or
/// otherwise backed) <c>{package}/skills/{skill}.md</c> +
/// <c>{package}/skills/{skill}.tools.json</c> pair.
/// </summary>
/// <param name="PackageName">Package identifier the bundle was resolved from (e.g. <c>spring-voyage/software-engineering</c>).</param>
/// <param name="SkillName">Skill name within the package.</param>
/// <param name="Prompt">Markdown prompt fragment (the content of <c>{skill}.md</c>).</param>
/// <param name="RequiredTools">
/// Tool requirements declared in the companion <c>{skill}.tools.json</c>.
/// Empty when no <c>.tools.json</c> exists — a bundle may be prompt-only.
/// </param>
public record SkillBundle(
    string PackageName,
    string SkillName,
    string Prompt,
    IReadOnlyList<SkillToolRequirement> RequiredTools);