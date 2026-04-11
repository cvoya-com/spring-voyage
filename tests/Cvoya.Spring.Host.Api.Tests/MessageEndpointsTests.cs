// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using NSubstitute;

using Xunit;

public class MessageEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MessageEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_WhenAddressNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        // Directory returns null for this address, so routing fails with ADDRESS_NOT_FOUND.
        _factory.DirectoryService.ResolveAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "unknown-agent"),
            Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new SendMessageRequest(
            new AddressDto("agent", "unknown-agent"),
            "Domain",
            "conv-1",
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendMessage_WhenInvalidType_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SendMessageRequest(
            new AddressDto("agent", "test-agent"),
            "InvalidType",
            null,
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/messages", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}