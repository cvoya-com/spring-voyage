/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

public class HealthEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.GetAsync("/health", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>(ct);
        body!.Status.Should().Be("Healthy");
    }

    private record HealthResponse(string Status);
}
