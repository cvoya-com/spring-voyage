// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Directory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Provides directory services for address resolution and component registration.
/// </summary>
public interface IDirectoryService
{
    /// <summary>
    /// Resolves an address to a directory entry.
    /// </summary>
    /// <param name="address">The address to resolve.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The directory entry if found; otherwise, <c>null</c>.</returns>
    Task<DirectoryEntry?> ResolveAsync(Address address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a component in the directory.
    /// </summary>
    /// <param name="entry">The directory entry to register.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task RegisterAsync(DirectoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a component from the directory.
    /// </summary>
    /// <param name="address">The address of the component to unregister.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UnregisterAsync(Address address, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves all directory entries that match the specified role.
    /// Used for multicast delivery to role-based addresses.
    /// </summary>
    /// <param name="role">The role to search for.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of matching directory entries.</returns>
    Task<IReadOnlyList<DirectoryEntry>> ResolveByRoleAsync(string role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all registered directory entries.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A list of all directory entries.</returns>
    Task<IReadOnlyList<DirectoryEntry>> ListAllAsync(CancellationToken cancellationToken = default);
}