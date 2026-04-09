/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
}
