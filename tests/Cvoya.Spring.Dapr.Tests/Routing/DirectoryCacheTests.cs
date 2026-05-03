// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;

using Shouldly;

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
        var address = Address.For("agent", TestSlugIds.HexFor("engineering-team/ada"));
        var entry = CreateEntry(address);

        _cache.Set(address, entry);

        _cache.TryGet(address, out var result).ShouldBeTrue();
        result.ShouldBe(entry);
    }

    [Fact]
    public void Invalidate_removes_entry()
    {
        var address = Address.For("agent", TestSlugIds.HexFor("engineering-team/ada"));
        var entry = CreateEntry(address);

        _cache.Set(address, entry);
        _cache.Invalidate(address);

        _cache.TryGet(address, out _).ShouldBeFalse();
    }

    [Fact]
    public void Clear_removes_all_entries()
    {
        var address1 = Address.For("agent", TestSlugIds.HexFor("team/ada"));
        var address2 = Address.For("connector", TestSlugIds.HexFor("team/github"));

        _cache.Set(address1, CreateEntry(address1));
        _cache.Set(address2, CreateEntry(address2));

        _cache.Clear();

        _cache.TryGet(address1, out _).ShouldBeFalse();
        _cache.TryGet(address2, out _).ShouldBeFalse();
    }

    private static DirectoryEntry CreateEntry(Address address) =>
        new(address, address.Id, "Test", "Test entry", null, DateTimeOffset.UtcNow);
}