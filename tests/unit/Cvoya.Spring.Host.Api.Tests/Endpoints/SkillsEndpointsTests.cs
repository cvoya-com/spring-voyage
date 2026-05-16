// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the skills catalog endpoint
/// (<c>GET /api/v1/skills</c>) and the per-agent skill routes
/// (<c>GET / PUT /api/v1/agents/{id}/skills</c>).
/// </summary>
public class SkillsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ActorAda_Id = new("00002711-bbbb-cccc-dddd-000000000000");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SkillsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListSkills_ReturnsGitHubToolsFromRegistry()
    {
        // The test factory wires the real GitHubSkillRegistry through DI,
        // so the catalog surfaces the connector's tools. Assert on shape
        // (non-empty, well-formed entries) rather than the exact tool list
        // so adding a new GitHub tool does not break the test.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/tenant/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entries = await response.Content.ReadFromJsonAsync<List<SkillCatalogEntry>>(JsonOptions, ct);
        entries.ShouldNotBeNull();
        entries!.ShouldNotBeEmpty();
        entries.ShouldAllBe(e =>
            !string.IsNullOrEmpty(e.Name) &&
            !string.IsNullOrEmpty(e.Registry));
        entries.ShouldContain(e => e.Registry == "github");
    }

    [Fact]
    public async Task GetAgentSkills_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{Guid.NewGuid():N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentSkills_ReturnsConfiguredList()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeAgent("ada", ActorAda_Id,
            skills: ["github.read_file", "github.write_file"]);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{ActorAda_Id:N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentSkillsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Skills.ShouldBe(new[] { "github.read_file", "github.write_file" });

        await proxy.Received(1).GetSkillsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAgentSkills_ReplacesListAndReturnsUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeAgent("ada", ActorAda_Id,
            skills: ["github.read_file", "github.write_file"]);

        var body = new SetAgentSkillsRequest(
            new[] { "github.list_files", "github.create_branch" });

        var response = await _client.PutAsync(
            $"/api/v1/tenant/agents/{ActorAda_Id:N}/skills",
            JsonContent.Create(body, options: JsonOptions),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await proxy.Received(1).SetSkillsAsync(
            Arg.Is<string[]>(l =>
                l.Length == 2 &&
                l.Contains("github.list_files") &&
                l.Contains("github.create_branch")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAgentSkills_EmptyList_ClearsSkills()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = ArrangeAgent("ada", ActorAda_Id, skills: ["github.read_file"]);

        var body = new SetAgentSkillsRequest(Array.Empty<string>());

        var response = await _client.PutAsync(
            $"/api/v1/tenant/agents/{ActorAda_Id:N}/skills",
            JsonContent.Create(body, options: JsonOptions),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await proxy.Received(1).SetSkillsAsync(
            Arg.Is<string[]>(l => l.Length == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAgentSkills_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var body = new SetAgentSkillsRequest(new[] { "github.read_file" });
        var response = await _client.PutAsync(
            $"/api/v1/tenant/agents/{Guid.NewGuid():N}/skills",
            JsonContent.Create(body, options: JsonOptions),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private IAgentActor ArrangeAgent(string displayName, Guid actorId, string[] skills)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("agent", actorId),
            actorId,
            displayName,
            $"Agent {displayName}",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == actorId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IAgentActor>();
        proxy.GetSkillsAsync(Arg.Any<CancellationToken>()).Returns(skills);
        var actorIdStr = actorId.ToString("N");
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(a => a.GetId() == actorIdStr),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }
}
