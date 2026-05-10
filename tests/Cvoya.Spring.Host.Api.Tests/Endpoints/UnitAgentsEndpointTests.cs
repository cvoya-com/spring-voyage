// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the unit-scoped agent endpoints
/// (<c>GET / POST / DELETE /api/v1/units/{id}/agents[/{agentId}]</c>) and
/// the <c>PATCH /api/v1/agents/{id}</c> metadata route. In C2b-1 the
/// assign/unassign paths now read/write the <c>IUnitMembershipRepository</c>
/// instead of enforcing a 1:N parent-unit invariant.
/// </summary>
public class UnitAgentsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitDisplayName = "engineering";

    // Stable UUID used as the "engineering" unit's ActorId (#1492: endpoints
    // now require Guid-parseable ActorIds for membership lookups).
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");

    // Post-#1629 URL paths carry the unit's Guid hex, not its display name.
    // Centralised here so test bodies stay readable.
    private static readonly string UnitName = UnitEngineeringUuid.ToString("N");
    private static readonly Guid UnitMarketingUuid = new("ee1ee111-0000-0000-0000-000000000002");
    private static readonly Guid UnitProductUuid = new("ee1ee111-0000-0000-0000-000000000003");
    private static readonly Guid AgentAdaUuid = new("aadaadaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentBabbageUuid = new("aadaadaa-0000-0000-0000-000000000002");
    private static readonly Guid AgentTuringUuid = new("aadaadaa-0000-0000-0000-000000000003");
    private static readonly Guid AgentForeignAdaUuid = new("aadaadaa-0000-0000-0000-000000000099");

    // Server uses JsonStringEnumConverter (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Tracks slug → UUID mappings set up by ArrangeUnit / ArrangeAgent so
    // UpsertMembershipAsync / GetMembershipAsync can resolve the right UUIDs
    // without duplicating the actorId constants at every call site.
    private readonly Dictionary<string, Guid> _slugToUuid
        = new(StringComparer.OrdinalIgnoreCase);

    // Accumulates all arranged directory entries so ListAllAsync returns a
    // consistent set (required for GetDerivedAgentMetadataAsync UUID→slug
    // resolution of ParentUnit).
    private readonly List<DirectoryEntry> _arrangedEntries = [];

    public UnitAgentsEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListUnitAgents_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListUnitAgents_ReturnsAgentMembersEnrichedWithMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        unitProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new Address[]
            {
                new("agent", AgentAdaUuid),
                new("unit", UnitMarketingUuid), // sub-unit member — must be filtered out
            });

        ArrangeAgent("ada", AgentAdaUuid,
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand));

        // Derived parent comes from the membership repository, not the actor
        // state — so arrange a membership row for this agent in this unit.
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents!.Count.ShouldBe(1);
        agents[0].Name.ShouldBe("ada");
        agents[0].Model.ShouldBe("claude-opus");
        agents[0].Specialty.ShouldBe("reviewer");
        agents[0].Enabled.ShouldBeTrue();
        agents[0].ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
        // ParentUnit derives from the unit's DisplayName via the directory
        // (legacy slug-compat field) — the directory entry was registered
        // with display name UnitDisplayName.
        agents[0].ParentUnit.ShouldBe(UnitDisplayName);
    }

    [Fact]
    public async Task ListUnitAgents_MultipleAgents_EnrichesSequentiallyWithoutDbContextRace()
    {
        // Regression for #600: the Skills settings tab calls
        // GET /api/v1/units/{id}/agents, which enriches each agent member
        // with a server-side membership lookup. A previous implementation
        // ran the per-agent lookups concurrently through the same scoped
        // SpringDbContext (via IUnitMembershipRepository.ListByAgentAsync),
        // which is not thread-safe and surfaced in production as HTTP 500
        // ("A second operation was started on this context instance...").
        //
        // The EF in-memory provider used by these integration tests does
        // NOT reliably trip the ConcurrencyDetector, so we can't rely on
        // it to expose the bug. Instead we wrap the shared DI-registered
        // membership repository with a probe that counts concurrent
        // in-flight ListByAgentAsync calls and asserts the peak stays at
        // 1. The probe wraps the real (in-memory-EF-backed) repository so
        // the rest of the endpoint's integration contract still runs.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        unitProxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new Address[]
            {
                new("agent", AgentAdaUuid),
                new("agent", AgentBabbageUuid),
                new("agent", AgentTuringUuid),
            });

        ArrangeAgent("ada", AgentAdaUuid,
            new AgentMetadata(Model: "claude-opus", Enabled: true));
        ArrangeAgent("babbage", AgentBabbageUuid,
            new AgentMetadata(Model: "gpt-4", Enabled: true));
        ArrangeAgent("turing", AgentTuringUuid,
            new AgentMetadata(Model: "claude-sonnet", Enabled: true));

        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync(UnitName, "babbage");
        await UpsertMembershipAsync(UnitName, "turing");

        // Swap the scoped repository for a concurrency-probing wrapper.
        // We scope the override to this test via a dedicated factory so we
        // don't perturb shared fixture state.
        using var probingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IUnitMembershipRepository));
                if (existing is not null)
                {
                    services.Remove(existing);
                }
                services.AddScoped<IUnitMembershipRepository>(sp =>
                {
                    var inner = ActivatorUtilities.CreateInstance<
                        Cvoya.Spring.Dapr.Data.UnitMembershipRepository>(sp);
                    return new ConcurrencyProbingMembershipRepository(inner);
                });
            });
        });
        var client = probingFactory.CreateClient();

        var response = await client.GetAsync($"/api/v1/tenant/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents!.Count.ShouldBe(3);
        agents.Select(a => a.Name).ShouldBe(new[] { "ada", "babbage", "turing" }, ignoreOrder: true);

        ConcurrencyProbingMembershipRepository.PeakConcurrency
            .ShouldBe(1,
                "enrichment must call the membership repository sequentially " +
                "so the scoped DbContext is never re-entered concurrently.");
    }

    [Fact]
    public async Task AssignUnitAgent_NewAgent_CreatesMembershipAndAddsMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var membership = await GetMembershipAsync(UnitName, "ada");
        membership.ShouldNotBeNull();
        membership!.Enabled.ShouldBeTrue();

        await unitProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentAdaUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_CrossTenantAgent_Returns404()
    {
        // #745: the tenant guard rejects the write when the agent is not
        // visible in the current tenant. The endpoint surfaces the
        // CrossTenantMembershipException as 404 so the caller never
        // learns that the id exists in a different tenant.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeAgent("foreign-ada", AgentForeignAdaUuid, new AgentMetadata());

        _factory.TenantGuard
            .EnsureSameTenantAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == UnitEngineeringUuid),
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentForeignAdaUuid),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new CrossTenantMembershipException(
                new Address("unit", UnitEngineeringUuid),
                new Address("agent", AgentForeignAdaUuid),
                "cross-tenant")));

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentForeignAdaUuid:N}", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetMembershipAsync(UnitName, "foreign-ada")).ShouldBeNull();
        await unitProxy.DidNotReceive().AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_SameAgentInMultipleUnits_BothMembershipsExist()
    {
        // C2b-1 removes the 1:N conflict check. An agent may belong to any
        // number of units, and each membership is stored independently.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        ArrangeUnit();
        ArrangeUnit("marketing", UnitMarketingUuid);
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        (await _client.PostAsync($"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync($"/api/v1/tenant/units/{UnitMarketingUuid:N}/agents/{AgentAdaUuid:N}", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
    }

    [Fact]
    public async Task AssignUnitAgent_AgentAlreadyBelongsToThisUnit_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Re-asserting membership is harmless and makes the endpoint
        // safe to retry.
        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_InheritingAgent_DivergingParents_Returns422()
    {
        // ADR-0039 §6 / B3: an agent that inherits its execution config
        // (no own block) is being assigned to a second unit. The two parent
        // units' persisted defaults disagree on `agent` (the runtime
        // registry id) — the resolver flags it as a conflict and the
        // endpoint rejects with the structured 422 envelope so the CLI /
        // portal can surface "agent: engineering=claude, marketing=codex".
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit("marketing", UnitMarketingUuid);
        ArrangeUnit();
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        // Existing membership: ada belongs to engineering.
        await UpsertMembershipAsync(UnitName, "ada");

        // Engineering pins runtime "claude"; marketing pins runtime "codex".
        // The agent has no own execution block (default factory state — see
        // AgentExecutionStore.GetAsync returning null), so both fields are
        // up for inheritance.
        _factory.UnitExecutionStore
            .GetAsync(UnitEngineeringUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "claude")));
        _factory.UnitExecutionStore
            .GetAsync(UnitMarketingUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "codex")));

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitMarketingUuid:N}/agents/{AgentAdaUuid:N}", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        body.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");
        var conflictingFields = body.GetProperty("conflictingFields");
        conflictingFields.TryGetProperty("agent", out var agentField).ShouldBeTrue();
        agentField.GetArrayLength().ShouldBe(2);
        var values = agentField.EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        values.ShouldBe(new[] { "claude", "codex" });

        // The membership write must NOT have happened — a 422 has to leave
        // the directory and the membership table untouched so the operator
        // can fix the conflict and retry.
        (await GetMembershipAsync("marketing", "ada")).ShouldBeNull();
        await unitProxy.DidNotReceive().AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignUnitAgent_AgentHasOwnRuntime_DivergingParentsAccepted()
    {
        // Same parent-set divergence as the previous test, but the agent
        // declared its own `agent` (runtime id). The resolver's "agent
        // wins" rule suppresses the conflict for that field — the
        // assignment succeeds with the same 200 the no-conflict path
        // returns today.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit("marketing", UnitMarketingUuid);
        ArrangeUnit();
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");

        _factory.UnitExecutionStore
            .GetAsync(UnitEngineeringUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "claude")));
        _factory.UnitExecutionStore
            .GetAsync(UnitMarketingUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "codex")));

        // Agent declares its own runtime explicitly, so the resolver does
        // not consult either parent's `agent` slot for inheritance.
        _factory.AgentExecutionStore
            .GetAsync(AgentAdaUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(Agent: "spring-voyage")));

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitMarketingUuid:N}/agents/{AgentAdaUuid:N}", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentAdaUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemovesMembershipAndMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeUnit("marketing", UnitMarketingUuid);
        var agentProxy = ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();

        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentAdaUuid),
            Arg.Any<CancellationToken>());
        // Cached pointer tracks the surviving membership now — it must NOT
        // have been cleared, since the agent still belongs to marketing.
        await agentProxy.DidNotReceive().ClearParentUnitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemainingParentsDivergeForInheritedAgent_Returns422()
    {
        // ADR-0039 §6 / B4: unassigning engineering would leave ada with
        // marketing + product as remaining parents. Those two units disagree
        // on the inherited `agent` runtime id, and ada has no own execution
        // block, so the endpoint rejects before deleting anything.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeUnit("marketing", UnitMarketingUuid);
        ArrangeUnit("product", UnitProductUuid);
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");
        await UpsertMembershipAsync("product", "ada");

        _factory.UnitExecutionStore
            .GetAsync(UnitMarketingUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "claude")));
        _factory.UnitExecutionStore
            .GetAsync(UnitProductUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "codex")));

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        body.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");
        var conflictingFields = body.GetProperty("conflictingFields");
        conflictingFields.TryGetProperty("agent", out var agentField).ShouldBeTrue();
        agentField.GetArrayLength().ShouldBe(2);
        var values = agentField.EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        values.ShouldBe(new[] { "claude", "codex" });

        // Rejection happens before the write, so all rows and actor state
        // remain untouched.
        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
        (await GetMembershipAsync("product", "ada")).ShouldNotBeNull();
        await unitProxy.DidNotReceive().RemoveMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemainingParentsConsistent_Returns204()
    {
        // Same remaining-parent shape as the conflict test, but the two
        // surviving units agree on the inherited runtime id. The endpoint
        // accepts the unassign and deletes only the requested membership.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        ArrangeUnit("marketing", UnitMarketingUuid);
        ArrangeUnit("product", UnitProductUuid);
        ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");
        await UpsertMembershipAsync("product", "ada");

        _factory.UnitExecutionStore
            .GetAsync(UnitMarketingUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "claude")));
        _factory.UnitExecutionStore
            .GetAsync(UnitProductUuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: "claude")));

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
        (await GetMembershipAsync("product", "ada")).ShouldNotBeNull();
        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentAdaUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_NoRemainingParents_MakesAgentTopLevel()
    {
        // ADR-0039 §6 / B4: an empty remaining parent set is valid — the
        // agent becomes top-level and later resolves against tenant defaults.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var unitProxy = ArrangeUnit();
        var agentProxy = ArrangeAgent("ada", AgentAdaUuid, new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{UnitName}/agents/{AgentAdaUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == AgentAdaUuid),
            Arg.Any<CancellationToken>());
        await agentProxy.Received(1).ClearParentUnitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMember_CrossTenantUnit_Returns404()
    {
        // #745: adding a sub-unit from a different tenant must fail. The
        // endpoint wires the tenant guard before the actor call, so a
        // seeded rejection is surfaced as 404 without ever touching
        // actor state — cross-tenant members would be a data-leak path
        // because subsequent dispatch would reach into the foreign tenant.
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var foreignSubId = new Guid("ee1ee111-0000-0000-0000-000000000099");
        var unitProxy = ArrangeUnit();
        _factory.TenantGuard
            .EnsureSameTenantAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == UnitEngineeringUuid),
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == foreignSubId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new CrossTenantMembershipException(
                new Address("unit", UnitEngineeringUuid),
                new Address("unit", foreignSubId),
                "cross-tenant")));

        var body = new AddMemberRequest(new AddressDto("unit", foreignSubId.ToString("N")));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenant/units/{UnitName}/members")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await unitProxy.DidNotReceive().AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAgent_PartialFields_CallsSetMetadataAndReturnsMerged()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        var agentProxy = ArrangeAgent("ada", AgentAdaUuid,
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.Auto));

        var patch = new UpdateAgentMetadataRequest(Enabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/tenant/agents/{AgentAdaUuid:N}")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The endpoint forwards the partial patch verbatim; the actor handles
        // the "null means leave untouched" merge internally. Critically, the
        // endpoint must NOT pass ParentUnit — containment is only mutable via
        // the unit's assign / unassign routes.
        await agentProxy.Received(1).SetMetadataAsync(
            Arg.Is<AgentMetadata>(m =>
                m.Enabled == false &&
                m.Model == null &&
                m.Specialty == null &&
                m.ExecutionMode == null &&
                m.ParentUnit == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchAgent_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var patch = new UpdateAgentMetadataRequest(Model: "gpt-4");
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/tenant/agents/{Guid.NewGuid():N}")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var response = await _client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ClearAllMocks()
    {
        _slugToUuid.Clear();
        _arrangedEntries.Clear();
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<DirectoryEntry>>(_arrangedEntries.AsReadOnly()));

        // #745: reset the tenant-guard substitute back to allow-all so
        // tests that seed cross-tenant rejection don't bleed into the
        // next test via the shared factory.
        _factory.TenantGuard.ClearReceivedCalls();
        _factory.TenantGuard
            .ShareTenantAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _factory.TenantGuard
            .EnsureSameTenantAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // ADR-0039 §6 / B3: reset the inheritance-resolver inputs back to
        // their permissive defaults so a 422-conflict seed in one test
        // doesn't bleed into the next via the shared factory. The two
        // store substitutes default to null (no own config / no parent
        // defaults) which the resolver treats as "fully unset".
        _factory.AgentExecutionStore.ClearReceivedCalls();
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(null));
        _factory.UnitExecutionStore.ClearReceivedCalls();
        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(null));

        // Each test gets a fresh scoped repository view via the DI container;
        // the underlying in-memory DB is per-factory but we clear rows here
        // so tests don't leak rows into each other.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    private IUnitActor ArrangeUnit(string? name = null, Guid actorUuid = default)
    {
        name ??= UnitDisplayName;
        var uuid = actorUuid == default ? UnitEngineeringUuid : actorUuid;
        var actorId = uuid.ToString("N");
        _slugToUuid[$"unit:{name}"] = uuid;
        // Also key by the Guid hex so URL-shaped identifiers used by tests
        // (e.g. UnitName = UnitEngineeringUuid.ToString("N")) resolve to the
        // same UUID for membership lookups.
        _slugToUuid[$"unit:{actorId}"] = uuid;

        var entry = new DirectoryEntry(
            new Address("unit", uuid),
            uuid,
            name,
            $"{name} unit",
            null,
            DateTimeOffset.UtcNow);

        _arrangedEntries.RemoveAll(e => e.Address.Scheme == "unit" && e.Address.Id == uuid);
        _arrangedEntries.Add(entry);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == uuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();

        // #2072: the assign / unassign endpoints route membership writes
        // through UnitActor.AddMemberAsync / RemoveMemberAsync (the
        // canonical surface post-#2052). The mocked proxy can't run the
        // real coordinator, so wire its add / remove calls to the same
        // EF-backed repository the tests' arrange / assert helpers use.
        // This keeps these endpoint tests focused on the endpoint's
        // policy (cross-tenant guard, multi-parent inheritance check,
        // cached-pointer refresh) while still exercising the membership-
        // row write through to EF.
        proxy
            .AddMemberAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var member = callInfo.Arg<Address>();
                if (string.Equals(member.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    using var scope = _factory.Services.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
                    return repo.UpsertAsync(
                        new UnitMembership(uuid, member.Id, Enabled: true),
                        callInfo.Arg<CancellationToken>());
                }
                return Task.CompletedTask;
            });
        proxy
            .RemoveMemberAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var member = callInfo.Arg<Address>();
                if (!string.Equals(member.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.CompletedTask;
                }

                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider
                    .GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
                var ct = callInfo.Arg<CancellationToken>();
                var existing = db.UnitMemberships.FirstOrDefault(
                    m => m.UnitId == uuid && m.AgentId == member.Id);
                if (existing is null)
                {
                    return Task.CompletedTask;
                }
                db.UnitMemberships.Remove(existing);
                return db.SaveChangesAsync(ct);
            });

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private IAgentActor ArrangeAgent(string agentId, Guid actorUuid, AgentMetadata metadata)
    {
        var actorIdStr = actorUuid.ToString("N");
        _slugToUuid[$"agent:{agentId}"] = actorUuid;

        var entry = new DirectoryEntry(
            new Address("agent", actorUuid),
            actorUuid,
            agentId,
            $"Agent {agentId}",
            null,
            DateTimeOffset.UtcNow);

        _arrangedEntries.RemoveAll(e => e.Address.Scheme == "agent" && e.Address.Id == actorUuid);
        _arrangedEntries.Add(entry);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == actorUuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IAgentActor>();
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>()).Returns(metadata);
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(a => a.GetId() == actorIdStr),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    /// <summary>
    /// Seeds a membership row using the UUIDs registered by
    /// <see cref="ArrangeUnit"/> and <see cref="ArrangeAgent"/>.
    /// Falls back to a new UUID when the slug has no corresponding
    /// arranged entry (ghost-agent / ghost-unit scenarios).
    /// </summary>
    private async Task UpsertMembershipAsync(string unitName, string agentName)
    {
        var unitUuid = _slugToUuid.TryGetValue($"unit:{unitName}", out var uid) ? uid : Guid.NewGuid();
        var agentUuid = _slugToUuid.TryGetValue($"agent:{agentName}", out var aid) ? aid : Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new UnitMembership(unitUuid, agentUuid, Enabled: true),
            CancellationToken.None);
    }

    private async Task<UnitMembership?> GetMembershipAsync(string unitName, string agentName)
    {
        var unitUuid = _slugToUuid.TryGetValue($"unit:{unitName}", out var uid) ? uid : Guid.Empty;
        var agentUuid = _slugToUuid.TryGetValue($"agent:{agentName}", out var aid) ? aid : Guid.Empty;
        if (unitUuid == Guid.Empty || agentUuid == Guid.Empty)
        {
            return null;
        }
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        return await repo.GetAsync(unitUuid, agentUuid, CancellationToken.None);
    }

    /// <summary>
    /// Test wrapper around <see cref="IUnitMembershipRepository"/> that
    /// tracks the peak number of concurrent in-flight
    /// <see cref="IUnitMembershipRepository.ListByAgentAsync"/> calls. The
    /// endpoint under test must call the repository sequentially (so the
    /// scoped <see cref="Cvoya.Spring.Dapr.Data.SpringDbContext"/> is never
    /// re-entered concurrently); the assertion on <see cref="PeakConcurrency"/>
    /// pins that contract as a regression guard for #600.
    /// </summary>
    private sealed class ConcurrencyProbingMembershipRepository : IUnitMembershipRepository
    {
        private static int s_inFlight;
        private static int s_peak;

        public static int PeakConcurrency => Volatile.Read(ref s_peak);

        private readonly IUnitMembershipRepository _inner;

        public ConcurrencyProbingMembershipRepository(IUnitMembershipRepository inner)
        {
            _inner = inner;
            // Reset across test runs — the wrapper is re-created per scope
            // but the static counters have to start clean for each test.
            Interlocked.Exchange(ref s_inFlight, 0);
            Interlocked.Exchange(ref s_peak, 0);
        }

        public Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default)
            => _inner.UpsertAsync(membership, cancellationToken);

        public Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(unitId, agentId, cancellationToken);

        public Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => _inner.DeleteAllForAgentAsync(agentId, cancellationToken);

        public Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
            => _inner.GetAsync(unitId, agentId, cancellationToken);

        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
            => _inner.ListByUnitAsync(unitId, cancellationToken);

        public Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
            => _inner.ListAllAsync(cancellationToken);

        public async Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref s_inFlight);
            // Track the peak via a compare-and-swap loop so we don't race
            // on the max update.
            int observed;
            do
            {
                observed = Volatile.Read(ref s_peak);
                if (current <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref s_peak, current, observed) != observed);

            try
            {
                // Give parallel callers, if any, time to overlap so the
                // probe has a chance to observe concurrency.
                await Task.Delay(20, cancellationToken);
                return await _inner.ListByAgentAsync(agentId, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref s_inFlight);
            }
        }
    }
}
