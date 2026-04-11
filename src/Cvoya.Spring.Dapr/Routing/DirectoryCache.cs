// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// In-memory concurrent cache for directory entries.
/// Provides thread-safe lookup, storage, and invalidation of address-to-entry mappings.
/// Event-driven invalidation is stubbed for future implementation.
/// </summary>
public class DirectoryCache
{
    private readonly ConcurrentDictionary<string, DirectoryEntry> _entries = new();

    /// <summary>
    /// Attempts to retrieve a cached directory entry for the specified address.
    /// </summary>
    /// <param name="address">The address to look up.</param>
    /// <param name="entry">When this method returns, contains the cached entry if found; otherwise, the default value.</param>
    /// <returns><c>true</c> if a cached entry was found; otherwise, <c>false</c>.</returns>
    public bool TryGet(Address address, out DirectoryEntry entry)
    {
        return _entries.TryGetValue(ToKey(address), out entry!);
    }

    /// <summary>
    /// Stores a directory entry in the cache for the specified address.
    /// </summary>
    /// <param name="address">The address to cache.</param>
    /// <param name="entry">The directory entry to store.</param>
    public void Set(Address address, DirectoryEntry entry)
    {
        _entries[ToKey(address)] = entry;
    }

    /// <summary>
    /// Removes a cached entry for the specified address.
    /// </summary>
    /// <param name="address">The address to invalidate.</param>
    public void Invalidate(Address address)
    {
        _entries.TryRemove(ToKey(address), out _);
    }

    /// <summary>
    /// Removes all cached entries.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
    }

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";
}