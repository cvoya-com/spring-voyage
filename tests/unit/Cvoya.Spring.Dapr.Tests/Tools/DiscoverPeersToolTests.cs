// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Tools;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

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
        var adaId = new Guid("aaaaaaaa-1111-1111-1111-000000000001");
        var bobId = new Guid("aaaaaaaa-1111-1111-1111-000000000002");
        var entries = new List<DirectoryEntry>
        {
            new(
                new Address("agent", adaId),
                adaId,
                "Ada",
                "Backend engineer",
                "backend-engineer",
                DateTimeOffset.UtcNow),
            new(
                new Address("agent", bobId),
                bobId,
                "Bob",
                "Backend engineer",
                "backend-engineer",
                DateTimeOffset.UtcNow),
        };

        _directoryService.ResolveByRoleAsync("backend-engineer", Arg.Any<CancellationToken>())
            .Returns(entries);

        var parameters = JsonSerializer.SerializeToElement(new { role = "backend-engineer" });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.ValueKind.ShouldBe(JsonValueKind.Array);
        result.GetArrayLength().ShouldBe(2);
        result[0].GetProperty("DisplayName").GetString().ShouldBe("Ada");
        result[1].GetProperty("DisplayName").GetString().ShouldBe("Bob");
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

        result.ValueKind.ShouldBe(JsonValueKind.Array);
        result.GetArrayLength().ShouldBe(0);
    }
}
