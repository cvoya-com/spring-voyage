// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
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
    private const string UnitName = "engineering";
    private const string UnitActorId = "actor-engineering";

    // Server uses JsonStringEnumConverter (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

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

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/agents", ct);

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
                new("agent", "ada"),
                new("unit", "marketing"), // sub-unit member — must be filtered out
            });

        ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.OnDemand));

        // Derived parent comes from the membership repository, not the actor
        // state — so arrange a membership row for this agent in this unit.
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents!.Count.ShouldBe(1);
        agents[0].Name.ShouldBe("ada");
        agents[0].Model.ShouldBe("claude-opus");
        agents[0].Specialty.ShouldBe("reviewer");
        agents[0].Enabled.ShouldBeTrue();
        agents[0].ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
        agents[0].ParentUnit.ShouldBe(UnitName);
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
                new("agent", "ada"),
                new("agent", "babbage"),
                new("agent", "turing"),
            });

        ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(Model: "claude-opus", Enabled: true));
        ArrangeAgent("babbage", "actor-babbage",
            new AgentMetadata(Model: "gpt-4", Enabled: true));
        ArrangeAgent("turing", "actor-turing",
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

        var response = await client.GetAsync($"/api/v1/units/{UnitName}/agents", ct);

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
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var membership = await GetMembershipAsync(UnitName, "ada");
        membership.ShouldNotBeNull();
        membership!.Enabled.ShouldBeTrue();

        await unitProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
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
        ArrangeAgent("foreign-ada", "actor-foreign-ada", new AgentMetadata());

        _factory.TenantGuard
            .EnsureSameTenantAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "foreign-ada"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new CrossTenantMembershipException(
                new Address("unit", UnitName),
                new Address("agent", "foreign-ada"),
                "cross-tenant")));

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/foreign-ada", content: null, ct);

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
        ArrangeUnit("marketing", "actor-marketing");
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        (await _client.PostAsync($"/api/v1/units/{UnitName}/agents/ada", content: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.PostAsync("/api/v1/units/marketing/agents/ada", content: null, ct))
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
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.PostAsync(
            $"/api/v1/units/{UnitName}/agents/ada", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Re-asserting membership is harmless and makes the endpoint
        // safe to retry.
        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
        await unitProxy.Received(1).AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_RemovesMembershipAndMember()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        // #744: removing the last membership is no longer allowed — the
        // "other membership" case is the only one that still returns 204.
        // The solo-membership case is covered by
        // UnassignUnitAgent_LastMembership_Returns409 below.
        var unitProxy = ArrangeUnit();
        ArrangeUnit("marketing", "actor-marketing");
        var agentProxy = ArrangeAgent("ada", "actor-ada", new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();

        await unitProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "ada"),
            Arg.Any<CancellationToken>());
        // Cached pointer tracks the surviving membership now — it must NOT
        // have been cleared, since the agent still belongs to marketing.
        await agentProxy.DidNotReceive().ClearParentUnitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnassignUnitAgent_LastMembership_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        ArrangeUnit();
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());
        await UpsertMembershipAsync(UnitName, "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // Row must still exist — the invariant is enforced transactionally.
        (await GetMembershipAsync(UnitName, "ada")).ShouldNotBeNull();
    }

    [Fact]
    public async Task UnassignUnitAgent_OtherMembershipSurvives()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearAllMocks();

        ArrangeUnit();
        ArrangeUnit("marketing", "actor-marketing");
        ArrangeAgent("ada", "actor-ada", new AgentMetadata());

        await UpsertMembershipAsync(UnitName, "ada");
        await UpsertMembershipAsync("marketing", "ada");

        var response = await _client.DeleteAsync(
            $"/api/v1/units/{UnitName}/agents/ada", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetMembershipAsync(UnitName, "ada")).ShouldBeNull();
        (await GetMembershipAsync("marketing", "ada")).ShouldNotBeNull();
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

        var unitProxy = ArrangeUnit();
        _factory.TenantGuard
            .EnsureSameTenantAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "foreign-sub"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new CrossTenantMembershipException(
                new Address("unit", UnitName),
                new Address("unit", "foreign-sub"),
                "cross-tenant")));

        var body = new AddMemberRequest(new AddressDto("unit", "foreign-sub"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/units/{UnitName}/members")
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

        var agentProxy = ArrangeAgent("ada", "actor-ada",
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: true,
                ExecutionMode: AgentExecutionMode.Auto));

        var patch = new UpdateAgentMetadataRequest(Enabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/agents/ada")
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
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/agents/ghost")
        {
            Content = JsonContent.Create(patch, options: JsonOptions),
        };

        var response = await _client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ClearAllMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

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

        // Each test gets a fresh scoped repository view via the DI container;
        // the underlying in-memory DB is per-factory but we clear rows here
        // so tests don't leak rows into each other.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    private IUnitActor ArrangeUnit(string name = UnitName, string actorId = UnitActorId)
    {
        var entry = new DirectoryEntry(
            new Address("unit", name),
            actorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == name),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private IAgentActor ArrangeAgent(string agentId, string actorId, AgentMetadata metadata)
    {
        var entry = new DirectoryEntry(
            new Address("agent", agentId),
            actorId,
            agentId,
            $"Agent {agentId}",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == agentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IAgentActor>();
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>()).Returns(metadata);
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private async Task UpsertMembershipAsync(string unitId, string agentAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new UnitMembership(UnitId: unitId, AgentAddress: agentAddress, Enabled: true),
            CancellationToken.None);
    }

    private async Task<UnitMembership?> GetMembershipAsync(string unitId, string agentAddress)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        return await repo.GetAsync(unitId, agentAddress, CancellationToken.None);
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

        public Task DeleteAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(unitId, agentAddress, cancellationToken);

        public Task DeleteAllForAgentAsync(string agentAddress, CancellationToken cancellationToken = default)
            => _inner.DeleteAllForAgentAsync(agentAddress, cancellationToken);

        public Task<UnitMembership?> GetAsync(string unitId, string agentAddress, CancellationToken cancellationToken = default)
            => _inner.GetAsync(unitId, agentAddress, cancellationToken);

        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(string unitId, CancellationToken cancellationToken = default)
            => _inner.ListByUnitAsync(unitId, cancellationToken);

        public async Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(string agentAddress, CancellationToken cancellationToken = default)
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
                return await _inner.ListByAgentAsync(agentAddress, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref s_inFlight);
            }
        }
    }
}