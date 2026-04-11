// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using NSubstitute;

using Xunit;

public class DirectoryEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DirectoryEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListEntries_ReturnsAllDirectoryEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "First agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", "unit-1"), "actor-2", "Unit One", "First unit", null, DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/directory", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<List<DirectoryEntryResponse>>(ct);
        result.Should().HaveCount(2);
        result![0].Address.Scheme.Should().Be("agent");
        result[1].Address.Scheme.Should().Be("unit");
    }

    [Fact]
    public async Task FindByRole_ReturnsMatchingEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "First agent", "backend", DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ResolveByRoleAsync("backend", Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/directory/role/backend", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<List<DirectoryEntryResponse>>(ct);
        result.Should().HaveCount(1);
        result![0].Role.Should().Be("backend");
    }
}