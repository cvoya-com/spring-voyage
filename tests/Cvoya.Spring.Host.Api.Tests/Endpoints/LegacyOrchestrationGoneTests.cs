// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;

using Shouldly;

using Xunit;

public class LegacyOrchestrationGoneTests
    : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LegacyOrchestrationGoneTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData(HttpMethodName.Post, "/api/v1/tenant/units/{0}/orchestration")]
    [InlineData(HttpMethodName.Get, "/api/v1/units/{0}/orchestration")]
    public async Task LegacyOrchestrationEndpoint_Returns410(
        HttpMethodName method,
        string pathTemplate)
    {
        var unitId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(
            ToHttpMethod(method),
            string.Format(pathTemplate, unitId));

        using var response = await _client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    private static HttpMethod ToHttpMethod(HttpMethodName method) => method switch
    {
        HttpMethodName.Get => HttpMethod.Get,
        HttpMethodName.Post => HttpMethod.Post,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
    };

    public enum HttpMethodName
    {
        Get,
        Post,
    }
}