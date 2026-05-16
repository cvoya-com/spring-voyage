// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Seam for the image-tier of <see cref="IToolGrantResolver"/> (#2335 Sub B).
/// The resolver calls this to read SDK-declared tools persisted on the
/// subject's <c>image_tools</c> column. The default implementation
/// registered by <c>AddCvoyaSpringDapr()</c> returns an empty list — the
/// column itself is added by Sub C (#2336), which also registers a real
/// implementation. Keeping the contract here lets the resolver merge the
/// image tier additively without taking an implementation dependency on
/// either Sub C's storage shape or the SDK introspection flow.
/// </summary>
/// <remarks>
/// Implementations must be defensive: when the column is absent, NULL,
/// or empty, return an empty list rather than throwing. The resolver
/// folds the result into the effective tool set with
/// <see cref="ToolProvenance.ImagePrefix"/>; an empty result is the
/// normal pre-Sub-C state.
/// </remarks>
public interface IImageToolsReader
{
    /// <summary>
    /// Returns the SDK-declared image tools for <paramref name="subject"/>.
    /// Each entry is a fully-qualified <c>&lt;namespace&gt;.&lt;tool_name&gt;</c>
    /// id paired with an optional human-readable description. Returns an
    /// empty list when the subject has no image-tier tools yet or when
    /// the column is not present (pre-Sub-C state).
    /// </summary>
    Task<IReadOnlyList<ImageToolEntry>> GetImageToolsAsync(
        Address subject,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One image-declared tool entry as surfaced by
/// <see cref="IImageToolsReader.GetImageToolsAsync"/>.
/// </summary>
/// <param name="Name">Canonical tool id (e.g. <c>acme.transcode_audio</c>).</param>
/// <param name="Description">
/// Tool description as reported by the SDK introspection endpoint.
/// May be empty when the SDK does not surface a description.
/// </param>
/// <param name="ImageDigest">
/// Image digest (e.g. <c>sha256:abc…</c>) the tool was sourced from,
/// surfaced through <see cref="EffectiveTool.Provenance"/> as
/// <c>"image:&lt;digest&gt;"</c>. Empty when the digest is unknown.
/// </param>
public sealed record ImageToolEntry(string Name, string Description, string ImageDigest);
