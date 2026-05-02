// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

/// <summary>
/// Represents a parsed artefact reference from a <c>package.yaml</c>. The
/// reference grammar is a flat string (ADR-0035 decision 3):
/// <list type="bullet">
///   <item><description>
///     <b>Bare name</b> — <c>sv-oss-design</c> — resolves within the current
///     package (units → <c>./units/sv-oss-design.yaml</c>, agents →
///     <c>./agents/sv-oss-design.yaml</c>, skills →
///     <c>./skills/sv-oss-design.md</c>, workflows →
///     <c>./workflows/sv-oss-design/</c>).
///   </description></item>
///   <item><description>
///     <b>Qualified name</b> — <c>spring-voyage-oss/architect</c> — resolves
///     cross-package via the catalog. The part before the first <c>/</c> is
///     the package name; the part after is the artefact name within that
///     package.
///   </description></item>
/// </list>
/// </summary>
/// <param name="RawValue">The original string from the manifest.</param>
/// <param name="PackageName">
/// For cross-package references the package portion (before the <c>/</c>).
/// <c>null</c> for bare (within-package) references.
/// </param>
/// <param name="ArtefactName">
/// The artefact name. For bare references this is the whole raw value; for
/// qualified references it is the part after the <c>/</c>.
/// </param>
/// <param name="Kind">The artefact type this reference points at.</param>
public record ArtefactReference(
    string RawValue,
    string? PackageName,
    string ArtefactName,
    ArtefactKind Kind)
{
    /// <summary>
    /// <c>true</c> when this reference points to another package (i.e.
    /// <see cref="PackageName"/> is not null).
    /// </summary>
    public bool IsCrossPackage => PackageName is not null;

    /// <summary>
    /// Parses a flat-string artefact reference from a manifest value.
    /// </summary>
    /// <param name="rawValue">The string value from the YAML field.</param>
    /// <param name="kind">The artefact type implied by the field name.</param>
    /// <returns>A parsed <see cref="ArtefactReference"/>.</returns>
    /// <exception cref="PackageParseException">
    /// Thrown when <paramref name="rawValue"/> is null, empty, or contains
    /// more than one <c>/</c> segment (which would be ambiguous).
    /// </exception>
    public static ArtefactReference Parse(string rawValue, ArtefactKind kind)
    {
        ArgumentNullException.ThrowIfNull(rawValue);

        var trimmed = rawValue.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new PackageParseException($"Artefact reference cannot be empty.");
        }

        var parts = trimmed.Split('/', 3);
        return parts.Length switch
        {
            1 => new ArtefactReference(trimmed, null, parts[0], kind),
            2 => new ArtefactReference(trimmed, parts[0], parts[1], kind),
            _ => throw new PackageParseException(
                $"Artefact reference '{trimmed}' is invalid: only one '/' separator is allowed " +
                $"(format: '<name>' or '<package>/<name>').")
        };
    }
}

/// <summary>
/// The kind (artefact type) a reference targets, used to derive the
/// within-package resolution path.
/// </summary>
public enum ArtefactKind
{
    /// <summary>Resolves to <c>./units/&lt;name&gt;.yaml</c>.</summary>
    Unit,

    /// <summary>Resolves to <c>./agents/&lt;name&gt;.yaml</c>.</summary>
    Agent,

    /// <summary>Resolves to <c>./skills/&lt;name&gt;.md</c>.</summary>
    Skill,

    /// <summary>Resolves to <c>./workflows/&lt;name&gt;/</c>.</summary>
    Workflow,
}