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
/// Endpoint tests for the <c>/v1/networks</c> surface added in Stage 2 of
/// #522 / #1063. The point of these tests is the wiring contract: the
/// dispatcher MUST forward the worker's create / remove calls verbatim to
/// <see cref="IContainerRuntime"/> so the worker container itself does not
/// need a podman/docker binding. The idempotence behavior these tests
/// assert against is owned by the runtime (see
/// <c>ProcessContainerRuntime.CreateNetworkAsync</c>); here we only assert
/// that the endpoint is the seam, not that it tries to be clever.
/// </summary>
public class NetworksEndpointsTests : IClassFixture<DispatcherWebApplicationFactory>
{
    private readonly DispatcherWebApplicationFactory _factory;

    public NetworksEndpointsTests(DispatcherWebApplicationFactory factory)
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
    public async Task PostNetworks_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/networks", new { name = "spring-net-x" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostNetworks_MissingName_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/networks", new { name = "" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().CreateNetworkAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostNetworks_Authorized_DelegatesToRuntime()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/networks", new { name = "spring-net-abc" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ContainerRuntime.Received(1).CreateNetworkAsync(
            "spring-net-abc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteNetwork_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync("/v1/networks/spring-net-x", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteNetwork_Authorized_DelegatesToRuntime()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.DeleteAsync("/v1/networks/spring-net-abc", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.ContainerRuntime.Received(1).RemoveNetworkAsync(
            "spring-net-abc", Arg.Any<CancellationToken>());
    }
}