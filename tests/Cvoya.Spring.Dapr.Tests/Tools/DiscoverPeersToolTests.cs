// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="DiscoverPeersTool"/>.
/// </summary>
public class DiscoverPeersToolTests
{
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DiscoverPeersTool _tool;

    public DiscoverPeersToolTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tool = new DiscoverPeersTool(_directoryService, _loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_WithMatchingEntries_ReturnsEntries()
    {
        var entries = new List<DirectoryEntry>
        {
            new(
                new Address("agent", "team/ada"),
                "actor-1",
                "Ada",
                "Backend engineer",
                "backend-engineer",
                DateTimeOffset.UtcNow),
            new(
                new Address("agent", "team/bob"),
                "actor-2",
                "Bob",
                "Backend engineer",
                "backend-engineer",
                DateTimeOffset.UtcNow)
        };

        _directoryService.ResolveByRoleAsync("backend-engineer", Arg.Any<CancellationToken>())
            .Returns(entries);

        var parameters = JsonSerializer.SerializeToElement(new { role = "backend-engineer" });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.ValueKind.Should().Be(JsonValueKind.Array);
        result.GetArrayLength().Should().Be(2);
        result[0].GetProperty("DisplayName").GetString().Should().Be("Ada");
        result[1].GetProperty("DisplayName").GetString().Should().Be("Bob");
    }

    [Fact]
    public async Task ExecuteAsync_NoMatches_ReturnsEmptyArray()
    {
        _directoryService.ResolveByRoleAsync("nonexistent-role", Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>());

        var parameters = JsonSerializer.SerializeToElement(new { role = "nonexistent-role" });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.ValueKind.Should().Be(JsonValueKind.Array);
        result.GetArrayLength().Should().Be(0);
    }
}
