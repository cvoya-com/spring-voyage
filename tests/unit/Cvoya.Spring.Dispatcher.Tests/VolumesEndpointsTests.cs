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
/// Endpoint tests for the <c>/v1/volumes</c> surface added in D3c (#1274).
/// Asserts that the dispatcher forwards workspace-volume operations verbatim
/// to <see cref="IContainerRuntime"/> so the worker container never needs a
/// podman/docker binding.
/// </summary>
public class VolumesEndpointsTests : IClassFixture<DispatcherWebApplicationFactory>
{
    private readonly DispatcherWebApplicationFactory _factory;

    public VolumesEndpointsTests(DispatcherWebApplicationFactory factory)
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

    // ── POST /v1/volumes ────────────────────────────────────────────────────

    [Fact]
    public async Task PostVolumes_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/volumes", new { name = "spring-ws-agent" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostVolumes_MissingName_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/volumes", new { name = "" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive()
            .EnsureVolumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostVolumes_Authorized_DelegatesToRuntime()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/volumes", new { name = "spring-ws-my-agent" }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.ContainerRuntime.Received(1).EnsureVolumeAsync(
            "spring-ws-my-agent", Arg.Any<CancellationToken>());
    }

    // ── DELETE /v1/volumes/{name} ───────────────────────────────────────────

    [Fact]
    public async Task DeleteVolume_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync(
            "/v1/volumes/spring-ws-agent", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteVolume_Authorized_DelegatesToRuntime()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.DeleteAsync(
            "/v1/volumes/spring-ws-my-agent", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.ContainerRuntime.Received(1).RemoveVolumeAsync(
            "spring-ws-my-agent", Arg.Any<CancellationToken>());
    }

    // ── GET /v1/volumes/{name}/metrics ─────────────────────────────────────

    [Fact]
    public async Task GetVolumeMetrics_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/v1/volumes/spring-ws-agent/metrics", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetVolumeMetrics_RuntimeReturnsNull_Returns404()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetVolumeMetricsAsync("spring-ws-absent", Arg.Any<CancellationToken>())
            .Returns((VolumeMetrics?)null);

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/volumes/spring-ws-absent/metrics", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVolumeMetrics_RuntimeReturnsMetrics_Returns200WithBody()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        var lastWrite = DateTimeOffset.UtcNow;
        _factory.ContainerRuntime
            .GetVolumeMetricsAsync("spring-ws-present", Arg.Any<CancellationToken>())
            .Returns(new VolumeMetrics(SizeBytes: 2048L, LastWrite: lastWrite));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/volumes/spring-ws-present/metrics", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<VolumeMetricsResponse>(
            TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body!.SizeBytes.ShouldBe(2048L);
    }

    // ── GET /v1/volumes?prefix= (#3005) ────────────────────────────────────

    [Fact]
    public async Task ListVolumes_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/v1/volumes?prefix=spring-ws-", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListVolumes_MissingPrefix_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/volumes", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive()
            .ListVolumesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListVolumes_Authorized_DelegatesToRuntimeAndReturnsNames()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ListVolumesAsync("spring-ws-", Arg.Any<CancellationToken>())
            .Returns(new List<string> { "spring-ws-a", "spring-ws-b" });

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/volumes?prefix=spring-ws-", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ListVolumesResponse>(
            TestContext.Current.CancellationToken);
        body.ShouldNotBeNull();
        body!.Names.ShouldBe(new[] { "spring-ws-a", "spring-ws-b" });
        await _factory.ContainerRuntime.Received(1)
            .ListVolumesAsync("spring-ws-", Arg.Any<CancellationToken>());
    }
}
