// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/platform/system/configuration</c>
/// surface (closes #1255 / C1.3). The system-configuration endpoint returns the
/// cached startup <c>ConfigurationReport</c>; no extra DI plumbing is required
/// because the validator is registered and runs at host startup.
/// </summary>
public class SystemConfigurationContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SystemConfigurationContractTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetSystemConfiguration_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/platform/system/configuration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/platform/system/configuration", "get", "200", body);
    }
}