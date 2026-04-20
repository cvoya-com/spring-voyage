// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>POST /api/v1/agent-runtimes/{id}/verify-baseline</c>
/// (#688). Unknown runtimes surface as 404; registered runtimes return
/// the parsed <see cref="ContainerBaselineCheckResponse"/>.
/// </summary>
public class AgentRuntimeBaselineEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AgentRuntimeBaselineEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task VerifyBaseline_UnknownRuntime_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync(
            "/api/v1/agent-runtimes/not-a-real-runtime/verify-baseline",
            content: null,
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VerifyBaseline_KnownRuntime_ReturnsResult()
    {
        // The real Claude runtime ships a meaningful VerifyContainerBaselineAsync
        // (CLI binary probe). We only assert the envelope — whether the
        // check itself passes depends on whether the test host has the
        // claude CLI installed, which we can't assume.
        var ct = TestContext.Current.CancellationToken;
        var response = await _client.PostAsync(
            "/api/v1/agent-runtimes/claude/verify-baseline",
            content: null,
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ContainerBaselineCheckResponse>(ct);
        body.ShouldNotBeNull();
        body!.RuntimeId.ShouldBe("claude");
        body.Errors.ShouldNotBeNull();
    }
}