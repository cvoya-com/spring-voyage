// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// In-memory implementation of <see cref="IDirectoryService"/> backed by a <see cref="DirectoryCache"/>.
/// Maintains a canonical store of directory entries and delegates lookups through the cache.
/// </summary>
public class DirectoryService(DirectoryCache cache, ILoggerFactory loggerFactory) : IDirectoryService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DirectoryService>();
    private readonly ConcurrentDictionary<string, DirectoryEntry> _entries = new();

    /// <inheritdoc />
    public Task RegisterAsync(DirectoryEntry entry, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var key = ToKey(entry.Address);
        _entries[key] = entry;
        cache.Set(entry.Address, entry);

        _logger.LogInformation("Registered directory entry for {Scheme}://{Path} with actor ID {ActorId}",
            entry.Address.Scheme, entry.Address.Path, entry.ActorId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnregisterAsync(Address address, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var key = ToKey(address);
        _entries.TryRemove(key, out _);
        cache.Invalidate(address);

        _logger.LogInformation("Unregistered directory entry for {Scheme}://{Path}",
            address.Scheme, address.Path);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DirectoryEntry?> ResolveAsync(Address address, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (cache.TryGet(address, out var cached))
        {
            return Task.FromResult<DirectoryEntry?>(cached);
        }

        var key = ToKey(address);
        if (_entries.TryGetValue(key, out var entry))
        {
            cache.Set(address, entry);
            return Task.FromResult<DirectoryEntry?>(entry);
        }

        return Task.FromResult<DirectoryEntry?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryEntry>> ResolveByRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var matches = _entries.Values
            .Where(e => string.Equals(e.Role, role, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(matches);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DirectoryEntry>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<DirectoryEntry>>(_entries.Values.ToList());
    }

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";
}