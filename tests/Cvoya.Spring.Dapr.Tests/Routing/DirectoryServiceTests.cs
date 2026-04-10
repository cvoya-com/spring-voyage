// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="DirectoryService"/>.
/// </summary>
public class DirectoryServiceTests
{
    private readonly DirectoryCache _cache = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly DirectoryService _service;

    public DirectoryServiceTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _service = new DirectoryService(_cache, _loggerFactory);
    }

    [Fact]
    public async Task RegisterAsync_and_ResolveAsync_returns_correct_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(address, "actor-1", "Ada", "Backend engineer", "backend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        var resolved = await _service.ResolveAsync(address, ct);

        resolved.Should().NotBeNull();
        resolved!.ActorId.Should().Be("actor-1");
        resolved.DisplayName.Should().Be("Ada");
    }

    [Fact]
    public async Task UnregisterAsync_and_ResolveAsync_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(address, "actor-1", "Ada", "Backend engineer", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);
        await _service.UnregisterAsync(address, ct);

        var resolved = await _service.ResolveAsync(address, ct);
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task ResolveByRoleAsync_returns_matching_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry1 = new DirectoryEntry(
            new Address("agent", "team/ada"), "actor-1", "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", "team/bob"), "actor-2", "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry3 = new DirectoryEntry(
            new Address("agent", "team/charlie"), "actor-3", "Charlie", "Designer", "frontend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry1, ct);
        await _service.RegisterAsync(entry2, ct);
        await _service.RegisterAsync(entry3, ct);

        var results = await _service.ResolveByRoleAsync("backend-engineer", ct);

        results.Should().HaveCount(2);
        results.Select(e => e.ActorId).Should().BeEquivalentTo(["actor-1", "actor-2"]);
    }
}
