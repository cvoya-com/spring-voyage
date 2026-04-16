// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the Ollama model-discovery proxy endpoint
/// (<c>GET /api/v1/ollama/models</c>).
/// </summary>
public class OllamaEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OllamaEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListModels_OllamaUnreachable_Returns503()
    {
        // The default OllamaOptions.BaseUrl points at a non-existent server in
        // test mode, so the proxy should return 503 with ProblemDetails.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/ollama/models", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("Ollama");
    }
}