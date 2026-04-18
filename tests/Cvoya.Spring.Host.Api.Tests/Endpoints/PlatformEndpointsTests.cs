// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the read-only platform-info endpoint
/// (<c>GET /api/v1/platform/info</c>). The endpoint is anonymous so the
/// About panel + <c>spring platform info</c> CLI verb work before a
/// caller has negotiated a token.
/// </summary>
public class PlatformEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public PlatformEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetInfo_ReturnsVersionAndLicense()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/platform/info", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<PlatformInfoResponse>(ct);
        payload.ShouldNotBeNull();
        payload.Version.ShouldNotBeNullOrWhiteSpace();
        payload.License.ShouldBe("LicenseRef-BSL-1.1");
    }

    [Theory]
    [InlineData("1.2.3+abc1234", "1.2.3", "abc1234")]
    [InlineData("1.2.3+abcdefghijklmnopqrstuvwxyz", "1.2.3", "abcdefghijkl")]
    [InlineData("1.2.3", "1.2.3", null)]
    [InlineData(null, "9.9.9", null)]
    public void SplitInformationalVersion_ParsesSemverWithOptionalCommit(
        string? informational,
        string expectedVersion,
        string? expectedHash)
    {
        var (version, hash) = PlatformEndpoints.SplitInformationalVersion(informational, "9.9.9");
        version.ShouldBe(expectedVersion);
        hash.ShouldBe(expectedHash);
    }
}