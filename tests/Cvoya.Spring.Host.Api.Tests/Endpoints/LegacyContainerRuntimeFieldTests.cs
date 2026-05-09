// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for ADR-0039 G6: the create / update wire DTOs no
/// longer carry <c>containerRuntime</c>, and any request that still
/// includes the field is rejected with a structured 400
/// <c>LegacyContainerRuntimeField</c> response carrying the migration
/// hint from ADR-0039 §9.
/// </summary>
public class LegacyContainerRuntimeFieldTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string LegacyContainerRuntimeCode = "LegacyContainerRuntimeField";

    private const string LegacyMigrationHint =
        "containerRuntime is removed in ADR-0039; the container runtime is platform configuration.";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LegacyContainerRuntimeFieldTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // The factory's substitutes are class-fixture singletons and
        // accumulate received calls across tests. Reset them here so
        // each test's `DidNotReceive` assertions only see calls made
        // inside that test.
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentExecutionStore.ClearReceivedCalls();
        _factory.UnitExecutionStore.ClearReceivedCalls();
        _factory.TenantGuard.ClearReceivedCalls();
    }

    // ---- PUT /api/v1/tenant/units/{id}/execution ----------------------------

    [Fact]
    public async Task PutUnitExecution_BodyWithoutContainerRuntime_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var unitId = unitGuid.ToString("N");

        ArrangeUnitDirectoryEntry(unitGuid);
        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Image: "ghcr.io/example/agent:latest", Agent: "spring-voyage")));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitId}/execution",
            new UnitExecutionResponse(Image: "ghcr.io/example/agent:latest", Runtime: "spring-voyage"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitExecutionResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body.Image.ShouldBe("ghcr.io/example/agent:latest");
        body.Runtime.ShouldBe("spring-voyage");
    }

    [Fact]
    public async Task PutUnitExecution_BodyWithContainerRuntime_Returns400LegacyContainerRuntimeField()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var unitId = unitGuid.ToString("N");

        ArrangeUnitDirectoryEntry(unitGuid);

        // Hand-craft the body so we can ship the removed key past the typed DTO.
        using var content = new StringContent(
            """{"image":"ghcr.io/example/agent:latest","containerRuntime":"docker","runtime":"spring-voyage"}""",
            Encoding.UTF8,
            "application/json");

        var response = await _client.PutAsync(
            $"/api/v1/tenant/units/{unitId}/execution",
            content,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertLegacyContainerRuntimeProblem(response, ct);

        // Store must NOT be touched on the rejection path — the legacy
        // check fires before any persistence work.
        await _factory.UnitExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<UnitExecutionDefaults>(), Arg.Any<CancellationToken>());
    }

    // ---- PUT /api/v1/tenant/agents/{id}/execution ---------------------------

    [Fact]
    public async Task PutAgentExecution_BodyWithoutContainerRuntime_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentId = agentGuid.ToString("N");

        ArrangeAgentDirectoryEntry(agentGuid);
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(
                    Image: "ghcr.io/example/agent:latest",
                    Hosting: "ephemeral",
                    Agent: "spring-voyage")));

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentId}/execution",
            new AgentExecutionResponse(
                Image: "ghcr.io/example/agent:latest",
                Runtime: "spring-voyage",
                Hosting: "ephemeral"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AgentExecutionResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body.Image.ShouldBe("ghcr.io/example/agent:latest");
        body.Runtime.ShouldBe("spring-voyage");
        body.Hosting.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task PutAgentExecution_BodyWithContainerRuntime_Returns400LegacyContainerRuntimeField()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentId = agentGuid.ToString("N");

        ArrangeAgentDirectoryEntry(agentGuid);

        using var content = new StringContent(
            """{"image":"ghcr.io/example/agent:latest","containerRuntime":"podman","runtime":"spring-voyage","hosting":"ephemeral"}""",
            Encoding.UTF8,
            "application/json");

        var response = await _client.PutAsync(
            $"/api/v1/tenant/agents/{agentId}/execution",
            content,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertLegacyContainerRuntimeProblem(response, ct);

        await _factory.AgentExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<AgentExecutionShape>(), Arg.Any<CancellationToken>());
    }

    // ---- POST /api/v1/tenant/agents (DefinitionJson) ------------------------

    [Fact]
    public async Task CreateAgent_DefinitionJsonWithLegacyContainerRuntime_Returns400LegacyContainerRuntimeField()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        ArrangeUnitDirectoryEntry(unitGuid);

        var definition = """
            {
              "execution": {
                "image": "ghcr.io/example/agent:latest",
                "containerRuntime": "docker",
                "runtime": "spring-voyage"
              }
            }
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/agents",
            new CreateAgentRequest(
                DisplayName: "Ada",
                Description: "Test agent",
                Role: null,
                UnitIds: new[] { unitGuid },
                DefinitionJson: definition),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertLegacyContainerRuntimeProblem(response, ct);
    }

    // ---- helpers ------------------------------------------------------------

    private void ArrangeUnitDirectoryEntry(Guid unitGuid)
    {
        // The PUT handler resolves by Address.For("unit", id) where id is
        // the route-segment string. Match any unit address so the test
        // doesn't have to mirror GuidFormatter's canonicalisation rules.
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == Address.UnitScheme), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.UnitScheme, unitGuid),
                unitGuid,
                "Test Unit",
                "Test unit",
                null,
                DateTimeOffset.UtcNow));

        _factory.TenantGuard
            .ShareTenantAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private void ArrangeAgentDirectoryEntry(Guid agentGuid)
    {
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == Address.AgentScheme), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.AgentScheme, agentGuid),
                agentGuid,
                "Test Agent",
                "Test agent",
                null,
                DateTimeOffset.UtcNow));
    }

    private static async Task AssertLegacyContainerRuntimeProblem(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("code").GetString().ShouldBe(LegacyContainerRuntimeCode);
        root.GetProperty("detail").GetString().ShouldBe(LegacyMigrationHint);
    }
}
