// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.UnitEndpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Handler-level tests for the unit-keyed skills endpoints
/// (<c>GET / PUT /api/v1/tenant/units/{id}/skills</c>) introduced in
/// #2276. Per ADR-0039 a unit is an agent, so its skills live in the
/// same agent-live-config store as leaf agents; the handler calls
/// <see cref="IAgentStateCoordinator"/> directly rather than going
/// through <c>IUnitActor</c>. The coordinator is substituted at the
/// factory boundary so these tests assert the endpoint contract without
/// hitting the EF live-config repository.
/// </summary>
public class UnitSkillsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitSkillsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetUnitSkills_KnownUnit_ReturnsConfiguredList()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid, "engineering");
        var actorIdStr = unitGuid.ToString("N");
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        _factory.AgentStateCoordinator
            .GetSkillsAsync(actorIdStr, Arg.Any<CancellationToken>())
            .Returns(new[] { "github.read_file", "github.write_file" });

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/skills", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentSkillsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Skills.ShouldBe(new[] { "github.read_file", "github.write_file" });
        await _factory.AgentStateCoordinator.Received(1)
            .GetSkillsAsync(actorIdStr, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnitSkills_UnknownUnit_Returns404WithProblemDetails()
    {
        // Arrange: directory returns null for the lookup.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Act
        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/skills", ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        // Coordinator must NOT be called on the unknown-unit path — the
        // handler short-circuits on the directory miss.
        await _factory.AgentStateCoordinator.DidNotReceive()
            .GetSkillsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUnitSkills_ReplacesListAndReturnsUpdated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid, "engineering");
        var actorIdStr = unitGuid.ToString("N");
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        // The endpoint reads back after writing — second GetSkillsAsync
        // call returns the new value.
        _factory.AgentStateCoordinator
            .GetSkillsAsync(actorIdStr, Arg.Any<CancellationToken>())
            .Returns(new[] { "github.list_files", "github.create_branch" });
        _factory.AgentStateCoordinator
            .SetSkillsAsync(
                actorIdStr,
                Arg.Any<string[]>(),
                Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        var request = new SetAgentSkillsRequest(
            new[] { "github.list_files", "github.create_branch" });
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/skills", request, JsonOptions, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentSkillsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Skills.ShouldBe(new[] { "github.list_files", "github.create_branch" });

        await _factory.AgentStateCoordinator.Received(1).SetSkillsAsync(
            actorIdStr,
            Arg.Is<string[]>(l =>
                l.Length == 2 &&
                l.Contains("github.list_files") &&
                l.Contains("github.create_branch")),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUnitSkills_EmptyList_ClearsSkills()
    {
        // Arrange: empty list is a legitimate "clear" request.
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid, "engineering");
        var actorIdStr = unitGuid.ToString("N");
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        _factory.AgentStateCoordinator
            .GetSkillsAsync(actorIdStr, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        // Act
        var request = new SetAgentSkillsRequest(Array.Empty<string>());
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/skills", request, JsonOptions, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.AgentStateCoordinator.Received(1).SetSkillsAsync(
            actorIdStr,
            Arg.Is<string[]>(l => l.Length == 0),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUnitSkills_OmittedSkillsField_Returns400AndDoesNotCallCoordinator()
    {
        // Arrange — the request body is the empty JSON object `{}`. With
        // `Skills` left null the handler must reject with 400 ProblemDetails
        // and never reach the coordinator (it is the explicit guard at the
        // top of SetUnitSkillsAsync).
        var ct = TestContext.Current.CancellationToken;
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        var unitGuid = Guid.NewGuid();

        // Act — send raw `{}` so System.Text.Json leaves `Skills` at its
        // record-default of null. PutAsJsonAsync on a typed instance would
        // serialise the default array, defeating the test.
        using var content = new StringContent("{}",
            System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PutAsync(
            $"/api/v1/tenant/units/{unitGuid:N}/skills", content, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("status").GetInt32().ShouldBe(400);

        // Coordinator must NOT be invoked when the body is rejected up
        // front. This is the "no side effect on validation failure" pin.
        await _factory.AgentStateCoordinator.DidNotReceive().SetSkillsAsync(
            Arg.Any<string>(),
            Arg.Any<string[]>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUnitSkills_UnknownUnit_Returns404AndDoesNotCallCoordinator()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentStateCoordinator.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Act
        var request = new SetAgentSkillsRequest(new[] { "github.read_file" });
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/skills",
            request, JsonOptions, ct);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.AgentStateCoordinator.DidNotReceive().SetSkillsAsync(
            Arg.Any<string>(),
            Arg.Any<string[]>(),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>());
    }

    private void ArrangeUnitDirectoryEntry(Guid unitGuid, string displayName)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        var entry = new DirectoryEntry(
            new Address("unit", unitGuid),
            unitGuid,
            displayName,
            $"unit {displayName}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitGuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);
    }
}

/// <summary>
/// Authorisation tests for the unit-keyed skills endpoints. Without
/// <c>LocalDev</c> the host runs <c>ApiTokenScheme</c> and a missing
/// token must 401 before any handler logic executes.
/// </summary>
public class UnitSkillsEndpointsUnauthenticatedTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public UnitSkillsEndpointsUnauthenticatedTests()
    {
        var dbName = $"SkillsAuthTestDb_{Guid.NewGuid()}";
        var directoryService = Substitute.For<IDirectoryService>();
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.ConfigureServices(services =>
                {
                    UnauthenticatedTestHostHelpers.ReplaceDbAndRuntime(
                        services,
                        dbName,
                        directoryService,
                        actorProxyFactory,
                        agentProxyResolver);
                });
            });
    }

    public void Dispose()
    {
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetSkills_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SetSkills_MissingToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/skills",
            new SetAgentSkillsRequest(new[] { "github.read_file" }),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
