// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Execution;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint tests for the <c>/v1/images/pull</c> surface added in Stage 2 of
/// #522 / #1063. The endpoint closes a latent gap: the
/// <c>DispatcherClientContainerRuntime.PullImageAsync</c> POST already
/// targeted this URL, but the dispatcher never mapped the route so every
/// pull silently 404'd. These tests pin both the contract
/// (<c>TimeoutException</c> ↔ HTTP 504, <c>InvalidOperationException</c> ↔
/// HTTP 502) and the wiring so the worker's
/// <c>PullImageActivity</c> classification stays intact end-to-end.
/// </summary>
public class ImagesEndpointsTests : IClassFixture<DispatcherWebApplicationFactory>
{
    private readonly DispatcherWebApplicationFactory _factory;

    public ImagesEndpointsTests(DispatcherWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DispatcherWebApplicationFactory.ValidToken);
        return client;
    }

    [Fact]
    public async Task PostImagesPull_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/images/pull", new { image = "alpine:latest" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostImagesPull_MissingImage_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/images/pull", new { image = "" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().PullImageAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostImagesPull_Authorized_PassesImageAndTimeout()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/images/pull",
            new { image = "ghcr.io/cvoya/claude:1.2.3", timeoutSeconds = 60 },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ContainerRuntime.Received(1).PullImageAsync(
            "ghcr.io/cvoya/claude:1.2.3",
            TimeSpan.FromSeconds(60),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostImagesPull_TimeoutFromRuntime_MapsTo504()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("registry too slow")));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/images/pull",
            new { image = "alpine:latest" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
    }

    [Fact]
    public async Task PostImagesPull_RuntimeFailure_MapsTo502()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .PullImageAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("manifest unknown")));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/images/pull",
            new { image = "alpine:does-not-exist" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }
}