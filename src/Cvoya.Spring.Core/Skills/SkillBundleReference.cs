// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Identifies a skill bundle by its <see cref="Package"/> and
/// <see cref="Skill"/> coordinates. Mirrors the <c>{package, skill}</c> pair
/// declared in the <c>ai.skills</c> section of a unit manifest, but lives in
/// <c>Cvoya.Spring.Core</c> so resolvers and downstream code can depend on
/// the abstraction without pulling in the YAML layer.
/// </summary>
/// <param name="Package">Package identifier (e.g. <c>spring-voyage/software-engineering</c>).</param>
/// <param name="Skill">Skill name within the package (e.g. <c>triage-and-assign</c>).</param>
public record SkillBundleReference(string Package, string Skill);