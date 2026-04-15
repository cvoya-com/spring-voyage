// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Metadata for a single version of a secret, as surfaced by
/// <see cref="ISecretRegistry.ListVersionsAsync"/>. Mirrors the shape
/// an audit-log decorator or a version-browser UI would need to render
/// a per-version row: version number, origin, creation timestamp, and
/// a <see cref="IsCurrent"/> flag indicating whether this is the latest
/// (most-recent) version for its structural reference.
///
/// <para>
/// The opaque store key is intentionally omitted — the versions-list
/// surface is metadata-only and must never leak pointer material that
/// would make resolver short-circuits possible from outside the
/// resolver path. Callers that need the pointer go through
/// <see cref="ISecretRegistry.LookupAsync"/> (or the version-aware
/// overload) on the resolver path.
/// </para>
/// </summary>
/// <param name="Version">
/// The monotonically-increasing version number. Version numbers are
/// unique within a <see cref="SecretRef"/> and strictly increase on
/// each rotation.
/// </param>
/// <param name="Origin">
/// Who owns the slot this version points at. See
/// <see cref="SecretOrigin"/> for the full semantics.
/// </param>
/// <param name="CreatedAt">
/// The UTC timestamp when this version was registered (rotation time
/// for all versions beyond the first).
/// </param>
/// <param name="IsCurrent">
/// <c>true</c> when this version is the latest for its
/// <see cref="SecretRef"/> — the version a default resolve returns.
/// Exactly one entry per <see cref="SecretRef"/> has
/// <see cref="IsCurrent"/> set.
/// </param>
public record SecretVersionInfo(
    int Version,
    SecretOrigin Origin,
    DateTimeOffset CreatedAt,
    bool IsCurrent);