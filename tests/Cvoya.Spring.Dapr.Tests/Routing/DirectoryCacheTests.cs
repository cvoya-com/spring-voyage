// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;

using FluentAssertions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DirectoryCache"/>.
/// </summary>
public class DirectoryCacheTests
{
    private readonly DirectoryCache _cache = new();

    [Fact]
    public void Set_and_TryGet_round_trips_entry()
    {
        var address = new Address("agent", "engineering-team/ada");
        var entry = CreateEntry(address);

        _cache.Set(address, entry);

        _cache.TryGet(address, out var result).Should().BeTrue();
        result.Should().Be(entry);
    }

    [Fact]
    public void Invalidate_removes_entry()
    {
        var address = new Address("agent", "engineering-team/ada");
        var entry = CreateEntry(address);

        _cache.Set(address, entry);
        _cache.Invalidate(address);

        _cache.TryGet(address, out _).Should().BeFalse();
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var address1 = new Address("agent", "team/ada");
        var address2 = new Address("connector", "team/github");

        _cache.Set(address1, CreateEntry(address1));
        _cache.Set(address2, CreateEntry(address2));

        _cache.Clear();

        _cache.TryGet(address1, out _).Should().BeFalse();
        _cache.TryGet(address2, out _).Should().BeFalse();
    }

    private static DirectoryEntry CreateEntry(Address address) =>
        new(address, Guid.NewGuid().ToString(), "Test", "Test entry", null, DateTimeOffset.UtcNow);
}