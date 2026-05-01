// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Resolves between a human's stable UUID and their JWT username claim,
/// creating a new <c>Human</c> record on demand when the username is not
/// yet known. The resolver is the single boundary that converts every
/// incoming JWT claim into a UUID and vice versa for display.
/// </summary>
/// <remarks>
/// Implementations are scoped per-request (mirroring
/// <c>IParticipantDisplayNameResolver</c>) and cache both directions so
/// repeated calls within the same request never issue redundant database
/// round-trips. The default implementation is registered in
/// <c>Cvoya.Spring.Dapr</c> as a scoped service; the cloud overlay may
/// substitute a tenant-aware implementation via the <c>TryAdd*</c>-friendly
/// registration.
/// </remarks>
public interface IHumanIdentityResolver
{
    /// <summary>
    /// Returns the stable UUID for the supplied JWT username. When no
    /// <c>Human</c> row exists for the username, an upsert creates one
    /// and returns the new UUID. Never returns <see cref="Guid.Empty"/>
    /// for a non-null, non-empty username; throws when the database is
    /// unavailable.
    /// </summary>
    /// <param name="username">The JWT subject claim (NameIdentifier).</param>
    /// <param name="displayName">
    /// Optional display name to store when creating a new row. When
    /// <c>null</c> the username is used as the display name.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="username"/> is null or whitespace.
    /// </exception>
    Task<Guid> ResolveByUsernameAsync(
        string username,
        string? displayName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the username for the supplied UUID, or <c>null</c> when
    /// no <c>Human</c> row exists for that id.
    /// </summary>
    /// <param name="id">The stable UUID to look up.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<string?> GetUsernameAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the display name for the supplied UUID, or the username
    /// when no <c>Human</c> row exists for that id, or <c>null</c> when
    /// neither is available.
    /// </summary>
    /// <param name="id">The stable UUID to look up.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<string?> GetDisplayNameAsync(Guid id, CancellationToken cancellationToken = default);
}