// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

/// <summary>
/// Structural validation messages surfaced by the catalog walker for
/// filesystem-layout signals strict YAML parsing cannot catch (loose files
/// instead of folders, an inner artefact that declares <c>version:</c>,
/// a folder whose name disagrees with the inner <c>name:</c> field).
/// </summary>
/// <remarks>
/// Field-level legacy detection (pre-v0.1 shape hints, renamed sub-fields,
/// etc.) was retired in issue #2406 — strict YAML parsing on the typed
/// manifest classes catches unknown fields with a generic but actionable
/// error.
/// </remarks>
public static class Adr0043ParseErrors
{
    /// <summary>
    /// ADR-0043 §1: a file directly under <c>./agents/</c>, <c>./units/</c>,
    /// etc. (rather than a folder rooted at <c>package.yaml</c>) is rejected.
    /// </summary>
    public const string LegacyFlatArtefactLayout =
        "LegacyFlatArtefactLayout: artefact must be a folder rooted at `package.yaml`; " +
        "move `./agents/foo.yaml` to `./agents/foo/package.yaml`.";

    /// <summary>
    /// ADR-0043 §4: inner artefact <c>package.yaml</c> files do not declare
    /// <c>version:</c>; they inherit from the containing install-root Package.
    /// </summary>
    public const string UnexpectedInnerVersion =
        "UnexpectedInnerVersion: version: lives only on the install-root package.yaml; " +
        "inner artefacts inherit from the container.";

    /// <summary>
    /// ADR-0043 §3: the folder name must equal the <c>name:</c> field of its
    /// <c>package.yaml</c>.
    /// </summary>
    public const string ArtefactFolderNameMismatch =
        "ArtefactFolderNameMismatch: the folder name must equal the name: field of its " +
        "package.yaml; rename one to match.";

    /// <summary>
    /// ADR-0043 §3: a unit's <c>members:</c> bare <c>- agent:</c> /
    /// <c>- unit:</c> reference must resolve to an artefact owned by that
    /// unit — either nested under the unit's own <c>agents/</c> / <c>units/</c>
    /// folder, or synthesised from an inline body (ADR-0043 §5g). Bare names
    /// that resolve up to a top-level artefact or sideways into a sibling
    /// unit are rejected so the membership graph and the filesystem layout
    /// stay in agreement. Cross-package qualified references and template
    /// instantiation (<c>from:</c>) ride through unchanged.
    /// </summary>
    public const string UnitMemberOutOfScope =
        "UnitMemberOutOfScope: a unit's `- agent:` / `- unit:` member must resolve to an " +
        "artefact owned by that unit (nested under the unit's own agents/ or units/ folder, " +
        "or declared inline). Move the artefact under the unit's folder, declare it inline, " +
        "or qualify the reference as cross-package.";
}

/// <summary>
/// Thrown when a <c>package.yaml</c> cannot be parsed or fails structural
/// validation (malformed YAML, missing required fields, name collisions, etc.).
/// </summary>
public class PackageParseException : Exception
{
    /// <summary>Creates a new <see cref="PackageParseException"/>.</summary>
    public PackageParseException(string message) : base(message) { }

    /// <summary>Creates a new <see cref="PackageParseException"/> with an inner cause.</summary>
    public PackageParseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an artefact reference in a <c>package.yaml</c> cannot be
/// resolved — either the target package is unknown or the named artefact
/// does not exist within a known package. The <see cref="Reference"/>
/// property carries the exact string from the manifest so callers can
/// surface an actionable error.
/// </summary>
public class PackageReferenceNotFoundException : Exception
{
    /// <summary>Creates a new <see cref="PackageReferenceNotFoundException"/>.</summary>
    /// <param name="reference">The artefact reference that could not be resolved.</param>
    /// <param name="hint">Optional diagnostic hint (expected path, catalog search key, etc.).</param>
    public PackageReferenceNotFoundException(string reference, string? hint = null)
        : base(BuildMessage(reference, hint))
    {
        Reference = reference;
        Hint = hint;
    }

    /// <summary>The raw reference string from the manifest.</summary>
    public string Reference { get; }

    /// <summary>Diagnostic hint describing where the resolver searched.</summary>
    public string? Hint { get; }

    private static string BuildMessage(string reference, string? hint)
    {
        var msg = $"Artefact reference '{reference}' could not be resolved.";
        if (!string.IsNullOrWhiteSpace(hint))
        {
            msg += $" {hint}";
        }
        return msg;
    }
}

/// <summary>
/// Thrown when cycle detection finds a circular reference in the package's
/// artefact graph. The <see cref="CyclePath"/> property lists the
/// reference chain that forms the cycle, end-to-end, so operators can see
/// the exact offending edges.
/// </summary>
public class PackageCycleException : Exception
{
    /// <summary>Creates a new <see cref="PackageCycleException"/>.</summary>
    /// <param name="cyclePath">
    /// Ordered list of artefact names forming the cycle. The last element
    /// is the one that closes the cycle back to the first.
    /// </param>
    public PackageCycleException(IReadOnlyList<string> cyclePath)
        : base(BuildMessage(cyclePath))
    {
        CyclePath = cyclePath;
    }

    /// <summary>The ordered cycle path (the back-edge closes cycle[^1] → cycle[0]).</summary>
    public IReadOnlyList<string> CyclePath { get; }

    private static string BuildMessage(IReadOnlyList<string> path)
        => $"Circular artefact reference detected: {string.Join(" → ", path)} → {path[0]}";
}


/// <summary>
/// Thrown when an uploaded package YAML contains local (within-package) artefact
/// references that cannot be resolved without an on-disk package directory.
/// Uploaded packages must be self-contained in v0.1; multi-file tarball upload
/// is deferred to v0.2.
/// </summary>
public sealed class PackageUploadHasLocalRefException : PackageParseException
{
    /// <summary>Creates a new <see cref="PackageUploadHasLocalRefException"/>.</summary>
    /// <param name="localReferences">
    /// The bare artefact reference strings (e.g. <c>"unit: my-unit"</c>,
    /// <c>"agent: my-agent"</c>) that require an on-disk package directory.
    /// </param>
    public PackageUploadHasLocalRefException(IReadOnlyList<string> localReferences)
        : base(BuildMessage(localReferences))
    {
        LocalReferences = localReferences;
    }

    /// <summary>
    /// The local artefact references that cannot be resolved without a package
    /// directory. Each entry is formatted as <c>"&lt;kind&gt;: &lt;name&gt;"</c>.
    /// </summary>
    public IReadOnlyList<string> LocalReferences { get; }

    private static string BuildMessage(IReadOnlyList<string> refs) =>
        $"Uploaded package contains local references that cannot be resolved " +
        $"without a package directory: {string.Join(", ", refs)}. " +
        $"Multi-file packages are not yet supported via upload — install from " +
        $"the catalog instead, or supply a self-contained package YAML with no " +
        $"local references.";
}
