// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the connector-config endpoints
/// (<c>GET /api/v1/units/{id}/connector</c> and
/// <c>PUT /api/v1/units/{id}/connector</c>). Verifies the request shape is
/// forwarded to <see cref="IUnitActor.SetGitHubConfigAsync"/> and that 404 /
/// 400 paths return ProblemDetails as declared.
/// </summary>
public class UnitConnectorEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitConnectorEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetConnector_UnitNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/connector", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task GetConnector_NoConfigYet_ReturnsGithubShellWithDefaultEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns((UnitGitHubConfig?)null);
        proxy.GetGitHubHookIdAsync(Arg.Any<CancellationToken>())
            .Returns((long?)null);
        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/connector", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitConnectorResponse>(ct);
        body.ShouldNotBeNull();
        body!.Type.ShouldBe("github");
        body.Repo.ShouldBeNull();
        body.Events.ShouldContain("issues");
        body.Events.ShouldContain("pull_request");
        body.Events.ShouldContain("issue_comment");
        body.AppInstallationId.ShouldBeNull();
        body.WebhookId.ShouldBeNull();
    }

    [Fact]
    public async Task GetConnector_WithPersistedConfig_ReturnsRepoAndInstallationId()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetGitHubConfigAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitGitHubConfig("acme", "platform", 424242L, new[] { "push" }));
        proxy.GetGitHubHookIdAsync(Arg.Any<CancellationToken>())
            .Returns(12345L);
        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/connector", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitConnectorResponse>(ct);
        body.ShouldNotBeNull();
        body!.Repo.ShouldNotBeNull();
        body.Repo!.Owner.ShouldBe("acme");
        body.Repo.Name.ShouldBe("platform");
        body.AppInstallationId.ShouldBe(424242L);
        body.Events.ShouldBe(new[] { "push" });
        body.WebhookId.ShouldBe(12345L);
    }

    [Fact]
    public async Task SetConnector_HappyPath_PersistsConfigAndReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetGitHubHookIdAsync(Arg.Any<CancellationToken>())
            .Returns((long?)null);
        ArrangeResolved(proxy);

        var request = new SetUnitConnectorRequest(
            Type: "github",
            Repo: new UnitConnectorRepo("acme", "platform"),
            Events: new[] { "issues", "pull_request" },
            AppInstallationId: 99L);

        var response = await _client.PutAsJsonAsync($"/api/v1/units/{UnitName}/connector", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await proxy.Received(1).SetGitHubConfigAsync(
            Arg.Is<UnitGitHubConfig>(c =>
                c.Owner == "acme" &&
                c.Repo == "platform" &&
                c.AppInstallationId == 99L &&
                c.Events != null && c.Events.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetConnector_UnsupportedType_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SetUnitConnectorRequest(
            Type: "slack",
            Repo: new UnitConnectorRepo("acme", "platform"));

        var response = await _client.PutAsJsonAsync($"/api/v1/units/{UnitName}/connector", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task SetConnector_MissingRepo_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new SetUnitConnectorRequest(Type: "github", Repo: null);

        var response = await _client.PutAsJsonAsync($"/api/v1/units/{UnitName}/connector", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetConnector_UnitNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new SetUnitConnectorRequest(
            Type: "github",
            Repo: new UnitConnectorRepo("acme", "platform"));

        var response = await _client.PutAsJsonAsync($"/api/v1/units/{UnitName}/connector", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("unit", UnitName),
            ActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}