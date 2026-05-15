// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// PATCH-endpoint tests for the prompt-surface (#2293). Covers the
/// tri-state semantics of <c>instructions</c> on
/// <c>PATCH /api/v1/tenant/agents/{id}</c> and
/// <c>PATCH /api/v1/tenant/units/{id}</c>:
///
///   - String value      → replaces the slot, preserves siblings.
///   - Explicit JSON null → clears the slot, preserves siblings.
///   - Absent property    → leaves the slot untouched.
/// </summary>
public class InstructionsPatchEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    // Server serialises enums as strings (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public InstructionsPatchEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PatchAgent_StringInstructions_PersistedToDefinitionJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = SeedAgent("Ada", definition: null);

        var content = new StringContent(
            "{\"instructions\":\"Be precise.\"}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/agents/{agentId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var persisted = ReadAgentInstructions(agentId);
        persisted.ShouldBe("Be precise.");

        // The response body carries the new value too.
        var body = await response.Content.ReadFromJsonAsync<AgentResponse>(JsonOptions, ct);
        body!.Instructions.ShouldBe("Be precise.");
    }

    [Fact]
    public async Task PatchAgent_NullInstructions_ClearsSlot_PreservesSiblings()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingDef = JsonDocument.Parse(
            "{\"instructions\":\"old\",\"execution\":{\"runtime\":\"claude-code\"}}").RootElement.Clone();
        var agentId = SeedAgent("Ada", definition: existingDef);

        var content = new StringContent(
            "{\"instructions\":null}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/agents/{agentId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var definition = ReadAgentDefinition(agentId);
        definition.HasValue.ShouldBeTrue();
        definition!.Value.TryGetProperty("instructions", out _).ShouldBeFalse(
            "explicit null must remove the instructions key");
        definition.Value.TryGetProperty("execution", out var execution).ShouldBeTrue(
            "sibling properties must be preserved");
        execution.GetProperty("runtime").GetString().ShouldBe("claude-code");
    }

    [Fact]
    public async Task PatchAgent_AbsentInstructions_LeavesSlotUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingDef = JsonDocument.Parse(
            "{\"instructions\":\"original\"}").RootElement.Clone();
        var agentId = SeedAgent("Ada", definition: existingDef);

        // PATCH some other field (model) without addressing instructions.
        var content = new StringContent(
            "{\"model\":\"gpt-4o\"}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/agents/{agentId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var persisted = ReadAgentInstructions(agentId);
        persisted.ShouldBe("original");
    }

    [Fact]
    public async Task PatchAgent_StringInstructions_GetRoundTripsValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = SeedAgent("Ada", definition: null);

        var content = new StringContent(
            "{\"instructions\":\"Write tight code.\"}",
            Encoding.UTF8,
            "application/json");
        (await _client.PatchAsync($"/api/v1/tenant/agents/{agentId:N}", content, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // GET must surface the new value on AgentResponse.Instructions.
        var getResponse = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var agentEl = doc.RootElement.TryGetProperty("agent", out var inner)
            ? inner
            : doc.RootElement;
        agentEl.GetProperty("instructions").GetString().ShouldBe("Write tight code.");
    }

    [Fact]
    public async Task PatchUnit_StringInstructions_PersistedToDefinitionJson()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = SeedUnit("Engineering", definition: null);

        var content = new StringContent(
            "{\"instructions\":\"Members default to this.\"}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/units/{unitId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var persisted = ReadUnitInstructions(unitId);
        persisted.ShouldBe("Members default to this.");
    }

    [Fact]
    public async Task PatchUnit_NullInstructions_ClearsSlot_PreservesSiblings()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingDef = JsonDocument.Parse(
            "{\"instructions\":\"old\",\"expertise\":[]}").RootElement.Clone();
        var unitId = SeedUnit("Engineering", definition: existingDef);

        var content = new StringContent(
            "{\"instructions\":null}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/units/{unitId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var definition = ReadUnitDefinition(unitId);
        definition.HasValue.ShouldBeTrue();
        definition!.Value.TryGetProperty("instructions", out _).ShouldBeFalse();
        definition.Value.TryGetProperty("expertise", out _).ShouldBeTrue(
            "sibling properties must be preserved on clear");
    }

    [Fact]
    public async Task PatchUnit_AbsentInstructions_LeavesSlotUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var existingDef = JsonDocument.Parse(
            "{\"instructions\":\"keep me\"}").RootElement.Clone();
        var unitId = SeedUnit("Engineering", definition: existingDef);

        var content = new StringContent(
            "{\"model\":\"gpt-4o\"}",
            Encoding.UTF8,
            "application/json");
        var response = await _client.PatchAsync(
            $"/api/v1/tenant/units/{unitId:N}", content, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var persisted = ReadUnitInstructions(unitId);
        persisted.ShouldBe("keep me");
    }

    // --- Helpers ------------------------------------------------------

    private Guid SeedAgent(string displayName, JsonElement? definition)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new DirectoryEntry(
            new Address("agent", id), id, displayName, "Test agent", null, now);

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == id),
                Arg.Any<CancellationToken>())
            .Returns(entry);
        var actorProxy = Substitute.For<IAgentActor>();
        actorProxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentMetadata());
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(a => a.GetId() == id.ToString("N")),
                Arg.Any<string>())
            .Returns(actorProxy);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Id = id,
            TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            DisplayName = displayName,
            Definition = definition,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return id;
    }

    private Guid SeedUnit(string displayName, JsonElement? definition)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var entry = new DirectoryEntry(
            new Address("unit", id), id, displayName, "Test unit", null, now);

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == id),
                Arg.Any<CancellationToken>())
            .Returns(entry);
        var actorProxy = Substitute.For<IUnitActor>();
        actorProxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(UnitStatus.Draft);
        actorProxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == id.ToString("N")),
                Arg.Any<string>())
            .Returns(actorProxy);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = id,
            TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            DisplayName = displayName,
            Definition = definition,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.SaveChanges();
        return id;
    }

    private string? ReadAgentInstructions(Guid agentId)
    {
        var def = ReadAgentDefinition(agentId);
        if (def is { ValueKind: JsonValueKind.Object } d
            && d.TryGetProperty("instructions", out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private JsonElement? ReadAgentDefinition(Guid agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return db.AgentDefinitions.AsQueryable()
            .Where(a => a.Id == agentId && a.DeletedAt == null)
            .Select(a => a.Definition)
            .FirstOrDefault();
    }

    private string? ReadUnitInstructions(Guid unitId)
    {
        var def = ReadUnitDefinition(unitId);
        if (def is { ValueKind: JsonValueKind.Object } d
            && d.TryGetProperty("instructions", out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private JsonElement? ReadUnitDefinition(Guid unitId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return db.UnitDefinitions.AsQueryable()
            .Where(u => u.Id == unitId && u.DeletedAt == null)
            .Select(u => u.Definition)
            .FirstOrDefault();
    }
}
