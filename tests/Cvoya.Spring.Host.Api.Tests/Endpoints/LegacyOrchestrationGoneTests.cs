// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

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
    [InlineData("GET", "/api/v1/tenant/units/{0}/orchestration")]
    [InlineData("POST", "/api/v1/tenant/units/{0}/orchestration")]
    [InlineData("PUT", "/api/v1/tenant/units/{0}/orchestration")]
    [InlineData("DELETE", "/api/v1/tenant/units/{0}/orchestration")]
    [InlineData("GET", "/api/v1/units/{0}/orchestration")]
    [InlineData("POST", "/api/v1/units/{0}/orchestration")]
    [InlineData("PUT", "/api/v1/units/{0}/orchestration")]
    [InlineData("DELETE", "/api/v1/units/{0}/orchestration")]
    public async Task LegacyOrchestrationEndpoint_Returns410WithMigrationHint(
        string method,
        string pathTemplate)
    {
        var unitId = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            string.Format(pathTemplate, unitId));

        using var response = await _client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        root.GetProperty("title").GetString().ShouldBe("Orchestration endpoint removed");
        root.GetProperty("status").GetInt32().ShouldBe((int)HttpStatusCode.Gone);
        root.GetProperty("detail").GetString().ShouldBe(
            "The orchestration endpoint is removed in ADR-0039. Configure the unit's runtime instead (see docs/concepts/agents.md).");
    }
}