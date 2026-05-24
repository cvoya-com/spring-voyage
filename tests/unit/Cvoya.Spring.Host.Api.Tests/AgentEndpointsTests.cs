// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

public class AgentEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    // Server serialises enums as strings (Program.cs#134); tests must match.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // Stable UUID for the "engineering" unit actor (#1492: endpoints now
    // require Guid-parseable ActorIds for membership lookups).
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListAgents_ReturnsAgentsFromDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", agentId), agentId, "Test Agent", "A test agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", unitId), unitId, "Test Unit", "A test unit", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents!.Count().ShouldBe(1);
        // #2114: AgentResponse.Name carries the canonical 32-char no-dash hex
        // id (matches UnitResponse.Name); DisplayName carries the human label.
        agents![0].Id.ShouldBe(agentId);
        agents[0].Name.ShouldBe(agentId.ToString("N"));
        agents[0].DisplayName.ShouldBe("Test Agent");
        agents[0].Role.ShouldBe("backend");
    }

    // #2114: regression guard — AgentResponse.Name must carry the canonical
    // 32-char no-dash hex form of the actor id (matches UnitResponse.Name)
    // even when the agent's display name is slug-shaped or otherwise
    // address-looking. Pre-#2114 this field carried the display name,
    // creating an asymmetry with UnitResponse that broke single-helper
    // canonical-id extraction across the CLI / e2e surface.
    [Fact]
    public async Task ListAgents_AgentResponseName_IsCanonicalHexNeverDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", agentId), agentId, "Slug-Shaped Agent", "", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        var agent = agents!.ShouldHaveSingleItem();
        // Name is hex; DisplayName is the human label.
        agent.Name.ShouldBe(agentId.ToString("N"));
        agent.Name.ShouldNotBe("Slug-Shaped Agent");
        agent.DisplayName.ShouldBe("Slug-Shaped Agent");
    }

    [Fact]
    public async Task CreateAgent_RegistersAndReturnsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        // Clear any residual membership rows from previous tests that share
        // the IClassFixture in-memory DB.
        ClearMemberships();
        ArrangeUnitEntry("engineering", UnitEngineeringUuid);
        ArrangeAgentActorProxy();

        var request = new CreateAgentRequest(
            "New Agent", "A brand new agent", "frontend",
            UnitIds: new[] { UnitEngineeringUuid });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/api/v1/tenant/agents/");

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.DisplayName == "New Agent"),
            Arg.Any<CancellationToken>());

        // Verify the membership row was written. Agent UUID is assigned by
        // the endpoint (Guid.NewGuid()), so query by unit UUID and check any
        // row exists for the engineering unit (#1492).
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        var members = await repo.ListByUnitAsync(UnitEngineeringUuid, ct);
        members.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CreateAgent_EmptyUnitIds_RegistersTopLevelAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService.ClearReceivedCalls();
        ArrangeAgentActorProxy();

        var request = new CreateAgentRequest(
            "Orphan", "A top-level agent", "frontend",
            UnitIds: Array.Empty<Guid>());

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AgentResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.DisplayName == "Orphan"),
            Arg.Any<CancellationToken>());

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        var memberships = await repo.ListByAgentAsync(body.Id, ct);
        memberships.ShouldBeEmpty();
    }

    // ADR-0056 Wave 2 / #2657: a freshly-created agent picks up the
    // platform's default skill-bundle list out of the box. The endpoint
    // calls IAgentSkillBundleStore.AddAsync(actorId, conversational-
    // defaults) right after the auto-start gate so the agent's Layer 4
    // prompt carries the [PLATFORM CONTRACT — NON-NEGOTIABLE] fragment
    // on the very next dispatch — no follow-up `spring agent skills add`.
    [Fact]
    public async Task CreateAgent_TopLevel_AttachesConversationalDefaultsBundle()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentSkillBundleStore.ClearReceivedCalls();
        ArrangeAgentActorProxy();

        var request = new CreateAgentRequest(
            "Default-Bundle Agent", "Inherits the platform default bundle.", "frontend",
            UnitIds: Array.Empty<Guid>());

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The endpoint must call AddAsync exactly once per entry in
        // DefaultAgentSkillBundles.ForFreshAgent, using the canonical
        // package/skill coordinates the FileSystemSkillBundleResolver
        // expects on disk.
        await _factory.AgentSkillBundleStore.Received(1).AddAsync(
            Arg.Any<string>(),
            Arg.Is<Cvoya.Spring.Core.Skills.SkillBundleReference>(r =>
                r.Package == Cvoya.Spring.Core.Skills.DefaultAgentSkillBundles.ConversationalDefaults.Package &&
                r.Skill == Cvoya.Spring.Core.Skills.DefaultAgentSkillBundles.ConversationalDefaults.Skill),
            Arg.Any<CancellationToken>());
    }

    // The store substitute's default AddAsync returns null. A real
    // store would resolve via ISkillBundleResolver; if the resolver
    // throws (no packages tree configured, deployment suppressed the
    // bundle, etc.) the endpoint must still 201 — the directory entry
    // is already persisted and binding the default bundle is best-
    // effort. This pins that contract so a future change can't quietly
    // promote the warning to a hard failure.
    [Fact]
    public async Task CreateAgent_DefaultBundleResolveFails_StillReturns201()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentSkillBundleStore.ClearReceivedCalls();
        ArrangeAgentActorProxy();

        // Force the bundle store to throw on AddAsync. The endpoint
        // catches every non-cancellation exception by design.
        _factory.AgentSkillBundleStore
            .AddAsync(
                Arg.Any<string>(),
                Arg.Any<Cvoya.Spring.Core.Skills.SkillBundleReference>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Cvoya.Spring.Core.Skills.SkillBundle>>>(
                _ => throw new Cvoya.Spring.Core.Skills.SkillBundlePackageNotFoundException(
                    Cvoya.Spring.Core.Skills.DefaultAgentSkillBundles.ConversationalDefaults.Package,
                    "(simulated test failure)"));

        try
        {
            var request = new CreateAgentRequest(
                "Bundle-Resolve-Fails", "Test that 201 is preserved.", "frontend",
                UnitIds: Array.Empty<Guid>());

            var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }
        finally
        {
            // Reset the substitute's AddAsync behaviour so later tests
            // in this fixture aren't poisoned by the failure injection.
            _factory.AgentSkillBundleStore
                .AddAsync(
                    Arg.Any<string>(),
                    Arg.Any<Cvoya.Spring.Core.Skills.SkillBundleReference>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Cvoya.Spring.Core.Skills.SkillBundle>>(
                    Array.Empty<Cvoya.Spring.Core.Skills.SkillBundle>()));
        }
    }

    [Fact]
    public async Task CreateAgent_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var request = new CreateAgentRequest(
            "Lost", "Unit does not exist", "frontend",
            UnitIds: new[] { Guid.NewGuid() });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "agent"),
            Arg.Any<CancellationToken>());
    }

    private void ArrangeUnitEntry(string displayName, Guid actorId)
    {
        var entry = new DirectoryEntry(
            new Address("unit", actorId),
            actorId,
            displayName,
            $"unit {displayName}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == actorId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId.ToString("N")),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private void ArrangeAgentActorProxy()
    {
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IAgentActor>());
    }

    private void ClearMemberships()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    // -------------------------------------------------------------------
    // #1649: server-side search filters on GET /api/v1/tenant/agents.
    // The CLI's `agent show <name>` resolver (PR #1650) used to list
    // every agent and filter client-side. With ?display_name= and
    // ?unit_id= the resolver collapses to one round-trip per call.
    //
    // Each test seeds DirectoryService.ListAllAsync with three agents +
    // one unit, optionally seeds membership rows (real EF repo via the
    // in-memory DB), then asserts the wire-shape returned by the endpoint.
    // -------------------------------------------------------------------

    [Fact]
    public async Task ListAgents_DisplayNameFilter_NoMatch_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=ghost", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_OneMatch_ReturnsSingleAgent()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=Alice", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_CaseInsensitive_ReturnsMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=ALICE", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_DisplayNameFilter_MultipleMatches_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedAgentsWithDuplicateDisplayName();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?display_name=Alice", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(2);
        agents.ShouldAllBe(a => a.DisplayName == "Alice");
    }

    [Fact]
    public async Task ListAgents_UnitIdFilter_NarrowsToMembershipMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        var (alice, bob, _) = SeedThreeAgentsAndOneUnit();

        // Only Alice is a member of the engineering unit.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: alice,
                    Enabled: true),
                ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].Id.ShouldBe(alice);
    }

    [Fact]
    public async Task ListAgents_DisplayNameAndUnitIdFilters_Compose()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        var (alice, bob, _) = SeedThreeAgentsAndOneUnit();

        // Both Alice and Bob are in engineering, but display_name=Alice
        // narrows to one.
        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: alice,
                    Enabled: true),
                ct);
            await repo.UpsertAsync(
                new Cvoya.Spring.Core.Units.UnitMembership(
                    UnitId: UnitEngineeringUuid,
                    AgentId: bob,
                    Enabled: true),
                ct);
        }

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?display_name=Alice&unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.Count.ShouldBe(1);
        agents[0].Id.ShouldBe(alice);
        agents[0].DisplayName.ShouldBe("Alice");
    }

    [Fact]
    public async Task ListAgents_UnitIdFilter_NotMember_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        // No memberships seeded ⇒ the engineering unit has zero members.
        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents?unit_id={UnitEngineeringUuid:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAgents_MalformedUnitId_ReturnsEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        SeedThreeAgentsAndOneUnit();

        var response = await _client.GetAsync(
            "/api/v1/tenant/agents?unit_id=not-a-guid", ct);

        // Malformed unit_id is treated as "no match" rather than 400 — the
        // empty result is the canonical "no matches" wire shape and the CLI
        // never sends a malformed unit_id (it parses through GuidFormatter
        // before dispatching).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var agents = await response.Content.ReadFromJsonAsync<List<AgentResponse>>(JsonOptions, ct);
        agents.ShouldNotBeNull();
        agents.ShouldBeEmpty();
    }

    /// <summary>
    /// Seeds the directory mock with three agents (Alice / Bob / Carol)
    /// and the engineering unit. Returns the agents' Guids so individual
    /// tests can wire memberships through the real EF repo.
    /// </summary>
    private (Guid alice, Guid bob, Guid carol) SeedThreeAgentsAndOneUnit()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", alice), alice, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", bob), bob, "Bob", "bob", null, DateTimeOffset.UtcNow),
            new(new Address("agent", carol), carol, "Carol", "carol", null, DateTimeOffset.UtcNow),
            new(new Address("unit", UnitEngineeringUuid), UnitEngineeringUuid, "engineering", "eng", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);

        return (alice, bob, carol);
    }

    /// <summary>
    /// Seeds two agents that both carry the display_name "Alice" — used to
    /// verify the n-match path returns the full candidate list.
    /// </summary>
    private void SeedAgentsWithDuplicateDisplayName()
    {
        var aliceOne = Guid.NewGuid();
        var aliceTwo = Guid.NewGuid();
        var bob = Guid.NewGuid();

        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", aliceOne), aliceOne, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", aliceTwo), aliceTwo, "Alice", "alice", null, DateTimeOffset.UtcNow),
            new(new Address("agent", bob), bob, "Bob", "bob", null, DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(entries);
    }

    // -------------------------------------------------------------------
    // ADR-0039 §6 / plan task B1: multi-parent inheritance conflict on
    // POST /api/v1/tenant/agents. The endpoint resolves the agent's own
    // execution config against each parent unit's persisted defaults
    // (via IUnitExecutionStore + IExecutionConfigInheritanceResolver).
    // When any inherited field diverges across parents, the create is
    // rejected with the structured 422 documented in the ADR — the body
    // names the diverging field and lists each parent's contributed
    // value so the operator can either trim the parent set or set the
    // field explicitly on the agent.
    // -------------------------------------------------------------------

    [Fact]
    public async Task CreateAgent_MultiParentDivergingAgentRuntime_Returns422WithStructuredBody()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();

        // Two parent units that disagree on the inherited `agent` runtime slot.
        // The resolver short-circuits before any directory write, so we
        // never reach the agent register / membership upsert path.
        var unitDocker = new Guid("ee1ee111-0000-0000-0000-000000000010");
        var unitPodman = new Guid("ee1ee111-0000-0000-0000-000000000011");

        ArrangeUnitEntry("docker-fleet", unitDocker);
        ArrangeUnitEntry("podman-fleet", unitPodman);
        ArrangeAgentActorProxy();
        _factory.DirectoryService.ClearReceivedCalls();

        // Stub the per-unit execution defaults the resolver consults. The
        // CONVENTIONS.md identifier rule requires the canonical no-dash
        // hex form on the wire-side, which is the form the resolver passes
        // to IUnitExecutionStore.GetAsync.
        _factory.UnitExecutionStore
            .GetAsync(unitDocker.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Runtime: "claude-code")));
        _factory.UnitExecutionStore
            .GetAsync(unitPodman.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Runtime: "spring-voyage")));

        var request = new CreateAgentRequest(
            "Conflicted Agent",
            "Inherits a diverging runtime",
            "frontend",
            UnitIds: new[] { unitDocker, unitPodman });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        // 422 Unprocessable Entity per ADR-0039 §6 / plan B1 acceptance.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // The 422 body extends the problem-details shape with the
        // platform-specific `error` discriminator and `conflictingFields`
        // map. Parse as JsonDocument so the test asserts the wire shape
        // verbatim (no DTO indirection).
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;

        root.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");
        root.TryGetProperty("conflictingFields", out var fields).ShouldBeTrue();
        fields.TryGetProperty("runtime", out var runtimeArray).ShouldBeTrue();
        runtimeArray.ValueKind.ShouldBe(JsonValueKind.Array);

        var runtimeValues = runtimeArray
            .EnumerateArray()
            .Select(e => (
                UnitId: e.GetProperty("unitId").GetString(),
                Value: e.GetProperty("value").GetString()))
            .ToList();
        runtimeValues.Count.ShouldBe(2);
        runtimeValues.ShouldContain(p =>
            p.UnitId == unitDocker.ToString("N") && p.Value == "claude-code");
        runtimeValues.ShouldContain(p =>
            p.UnitId == unitPodman.ToString("N") && p.Value == "spring-voyage");

        // Conflict short-circuits before any side effect: nothing was
        // registered in the directory and no membership rows were written.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "agent"),
            Arg.Any<CancellationToken>());
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        var dockerMembers = await repo.ListByUnitAsync(unitDocker, ct);
        dockerMembers.ShouldBeEmpty();
        var podmanMembers = await repo.ListByUnitAsync(unitPodman, ct);
        podmanMembers.ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateAgent_MultiParentDivergingAgentRuntime_AgentSetsItExplicitly_Returns201()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();

        var unitDocker = new Guid("ee1ee111-0000-0000-0000-000000000020");
        var unitPodman = new Guid("ee1ee111-0000-0000-0000-000000000021");

        ArrangeUnitEntry("docker-fleet", unitDocker);
        ArrangeUnitEntry("podman-fleet", unitPodman);
        ArrangeAgentActorProxy();
        _factory.DirectoryService.ClearReceivedCalls();

        _factory.UnitExecutionStore
            .GetAsync(unitDocker.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Runtime: "claude-code")));
        _factory.UnitExecutionStore
            .GetAsync(unitPodman.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Runtime: "spring-voyage")));

        // Per ADR-0039 §6 rule 1 ("an explicit value on the agent always
        // wins"), declaring `execution.runtime` on the agent's own block
        // turns the slot into "agent-decided". The resolver no longer
        // reports a conflict on that field — the create succeeds.
        var definitionJson = """
            {
              "execution": {
                "runtime": "claude-code"
              }
            }
            """;

        var request = new CreateAgentRequest(
            "Resolved Agent",
            "Pins runtime explicitly",
            "frontend",
            UnitIds: new[] { unitDocker, unitPodman },
            DefinitionJson: definitionJson);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldContain("/api/v1/tenant/agents/");

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent" &&
                e.DisplayName == "Resolved Agent"),
            Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------
    // GET /api/v1/tenant/agents/{id}/runtime-status (#2100, #2440)
    //
    // The portal-facing runtime-status indicator. Backend covers four
    // states:
    //   - idle       (actor reports no channels, or persistent w/ no registry entry)
    //   - busy       (actor reports >0 in-flight channels)
    //   - queued     (actor reports 0 in-flight, >0 queued)
    //   - unavailable (persistent + registry entry present + probe Unhealthy)
    //
    // Per #2440 the agent policy mirrors the unit policy: "no registry
    // entry" on a persistent agent is the not-yet-deployed case and
    // falls through to the actor read (idle), not `unavailable`.
    // Ephemeral agents never flip to `unavailable` — there's no container
    // to probe — so we exercise the unavailable path through a persistent
    // shape on the execution-store substitute *with* a registered, marked-
    // Unhealthy entry.
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetAgentRuntimeStatus_AgentNotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghost = Guid.NewGuid();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Id == ghost), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{ghost:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_NoChannels_ReturnsIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Idle Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("idle");
        body.InFlightThreadCount.ShouldBe(0);
        body.QueuedMessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_InFlightChannel_ReturnsBusy()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Busy Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 1, queued: 0, channels: 1);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("busy");
        body.InFlightThreadCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_QueuedWithoutInFlight_ReturnsQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Queued Agent");
        // 0 in-flight + queued > 0 is the brief transient between drains.
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 3, channels: 1);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("queued");
        body.QueuedMessageCount.ShouldBe(3);
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_PersistentAgentNoRegistryEntry_ReturnsIdle()
    {
        // #2440: mirror the unit-side policy — "no registry entry" on a
        // persistent agent is the not-yet-deployed case which we report
        // as `idle` so the chip doesn't scream on every persistent agent
        // until a per-agent deploy lands. Lifecycle status is the source
        // of truth for "never deployed" visibility.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Persistent Not-Yet-Deployed");
        ArrangeAgentExecutionShape(agentId, hosting: "persistent");
        // No registry registration ⇒ TryGet returns false ⇒ fall through
        // to the actor read. Actor reports zero channels ⇒ idle.
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("idle");
        body.InFlightThreadCount.ShouldBe(0);
        body.QueuedMessageCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_PersistentAgentUnhealthy_ReturnsUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Persistent Sick");
        ArrangeAgentExecutionShape(agentId, hosting: "persistent");

        // ADR-0052 / #2618: deployment health is read from the execution host
        // over the gateway. A running-but-unhealthy persistent deployment
        // projects to `unavailable`.
        var actorId = agentId.ToString("N");
        _factory.ExecutionHostGateway
            .GetDeploymentAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState(
                AgentId: actorId,
                Running: true,
                HealthStatus: "unhealthy",
                Image: null,
                Endpoint: "http://test/agent",
                ContainerId: "container-1",
                StartedAt: DateTimeOffset.UtcNow,
                ConsecutiveFailures: 3));

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("unavailable");

        // Reset the shared substitute so a sibling test doesn't see this entry.
        _factory.ExecutionHostGateway
            .GetDeploymentAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Dapr.Execution.PersistentAgentDeploymentState.NotRunning(actorId));
    }

    [Fact]
    public async Task GetAgentRuntimeStatus_EphemeralAgentWithoutDeployment_FallsThroughToActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Ephemeral Idle");
        // No execution shape ⇒ ephemeral (default null hosting) ⇒ never
        // unavailable, even though the registry has no entry.
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/runtime-status", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentRuntimeStatusResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Status.ShouldBe("idle");
    }

    // -------------------------------------------------------------------
    // GET /api/v1/tenant/agents/{id} — effective-tools projection (#2337 Sub D).
    //
    // The Show endpoint projects IToolGrantResolver.ResolveAsync into the
    // wire response so the portal's Tools sub-tab can render the three-tier
    // layout (platform / connector / image) without re-deriving the grant
    // set. The factory's substitute resolver returns an empty list by
    // default; this test arranges a populated list and asserts the
    // projection lands on AgentDetailResponse.Agent.EffectiveTools.
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetAgent_EffectiveTools_PopulatedFromResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Tools Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        _factory.ToolGrantResolver
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == agentId),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Cvoya.Spring.Core.Skills.EffectiveTool>>(
                new[]
                {
                    new Cvoya.Spring.Core.Skills.EffectiveTool(
                        Name: "sv.expertise.lookup",
                        Namespace: "sv",
                        Description: "Look up a unit's expertise.",
                        Provenance: Cvoya.Spring.Core.Skills.ToolProvenance.Platform,
                        InheritedFromUnitName: null),
                    new Cvoya.Spring.Core.Skills.EffectiveTool(
                        Name: "github.create_issue",
                        Namespace: "github",
                        Description: "Open a new GitHub issue.",
                        Provenance: Cvoya.Spring.Core.Skills.ToolProvenance.ConnectorPrefix + "github",
                        InheritedFromUnitName: "engineering"),
                }));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Agent.EffectiveTools.ShouldNotBeNull();
        body.Agent.EffectiveTools!.Count.ShouldBe(2);

        var platform = body.Agent.EffectiveTools!.Single(t => t.Provenance == "platform");
        platform.Name.ShouldBe("sv.expertise.lookup");
        platform.Namespace.ShouldBe("sv");
        platform.InheritedFromUnitName.ShouldBeNull();

        var connector = body.Agent.EffectiveTools!.Single(t => t.Provenance == "connector:github");
        connector.Name.ShouldBe("github.create_issue");
        connector.Namespace.ShouldBe("github");
        connector.InheritedFromUnitName.ShouldBe("engineering");
    }

    [Fact]
    public async Task GetAgent_EffectiveTools_ResolverFailure_FailsOpenWithEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Tools Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        _factory.ToolGrantResolver
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == agentId),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Cvoya.Spring.Core.Skills.EffectiveTool>>(
                _ => throw new InvalidOperationException("resolver down"));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Agent.EffectiveTools.ShouldNotBeNull();
        body.Agent.EffectiveTools!.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------
    // GET /api/v1/tenant/agents/{id} — execution.image tag projection (#2348).
    //
    // The Show endpoint reads the agent's effective `execution.image`
    // slot through IAgentDefinitionProvider — the same path the
    // dispatcher uses. The merge with the parent unit's defaults (via
    // IUnitExecutionStore in #601 / #603) flows through automatically,
    // so the same resolution path covers both "agent declares image"
    // and "agent inherits image from parent unit".
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetAgent_ExecutionImage_PopulatedFromAgentDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Image Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        var definitionJson = JsonSerializer.SerializeToElement(new
        {
            execution = new
            {
                runtime = "claude",
                image = "acme/agent:v1.2",
            },
        });

        await SeedAgentDefinitionAsync(agentId, "Image Agent", definitionJson, ct);

        try
        {
            var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content
                .ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
            body.ShouldNotBeNull();
            body.Agent.ExecutionImage.ShouldBe("acme/agent:v1.2");
        }
        finally
        {
            await ClearAgentDefinitionAsync(agentId, ct);
        }
    }

    [Fact]
    public async Task GetAgent_ExecutionImage_InheritedFromParentUnit()
    {
        // The agent declares only the runtime id; the parent unit
        // declares the image. The provider's #601/#603 merge fills the
        // agent's image slot from the unit's defaults.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Inheriting Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        var agentDefinition = JsonSerializer.SerializeToElement(new
        {
            execution = new { runtime = "claude" },
        });
        await SeedAgentDefinitionAsync(agentId, "Inheriting Agent", agentDefinition, ct);
        await SeedAgentParentMembershipAsync(agentId, unitId, ct);

        // The provider reads the parent unit's execution defaults via
        // IUnitExecutionStore — the test factory's substitute returns
        // null by default, so arrange the inherited image here.
        _factory.UnitExecutionStore
            .GetAsync(unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Image: "acme/agent:v1.2")));

        try
        {
            var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.Content
                .ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
            body.ShouldNotBeNull();
            body.Agent.ExecutionImage.ShouldBe("acme/agent:v1.2");
        }
        finally
        {
            await ClearAgentDefinitionAsync(agentId, ct);
            await ClearMembershipsAsync(ct);
        }
    }

    [Fact]
    public async Task GetAgent_ExecutionImage_NullWhenNeitherAgentNorUnitDeclareImage()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "No Image Agent");
        ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);

        // No AgentDefinitionEntity seeded → provider returns null → field
        // collapses to JSON null. The envelope still renders so the Show
        // endpoint stays usable while the operator finishes configuration.
        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content
            .ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body.Agent.ExecutionImage.ShouldBeNull();
    }

    // -------------------------------------------------------------------
    // #2388: wire-contract regression — AgentResponse.lifecycleStatus must
    // serialize as the PascalCase LifecycleStatus enum name ("Draft" /
    // "Running" / "Stopped" / …) so the portal's action-button gates and
    // the Kiota-generated client see the same shape that UnitResponse.status
    // already emits. The earlier #2156 implementation lowercased the value
    // to dodge a nullable-enum oneOf ambiguity in the schema, which broke
    // the contract and the portal lifecycle gates downstream.
    // -------------------------------------------------------------------

    [Theory]
    [InlineData(Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Draft, "Draft")]
    [InlineData(Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Running, "Running")]
    [InlineData(Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Stopped, "Stopped")]
    [InlineData(Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Error, "Error")]
    public async Task GetAgent_LifecycleStatus_SerializesAsPascalCaseEnumName(
        Cvoya.Spring.Core.Lifecycle.LifecycleStatus status,
        string expectedWire)
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeAgentDirectoryEntry(agentId, "Lifecycle Wire Agent");
        var proxy = ArrangeAgentRuntimeStatus(agentId, inFlight: 0, queued: 0, channels: 0);
        proxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(status));

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentId:N}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Read the raw body so we can pin the exact wire string the portal
        // sees — round-tripping through AgentResponse would re-parse the
        // value and mask a casing drift.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var agent = doc.RootElement.GetProperty("agent");
        agent.TryGetProperty("lifecycleStatus", out var lifecycleStatus).ShouldBeTrue(
            "AgentResponse must include 'lifecycleStatus' on the wire");
        lifecycleStatus.ValueKind.ShouldBe(JsonValueKind.String);
        lifecycleStatus.GetString().ShouldBe(expectedWire);
    }

    private async Task SeedAgentDefinitionAsync(
        Guid agentId,
        string displayName,
        JsonElement definition,
        CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Id = agentId,
            TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            DisplayName = displayName,
            Definition = definition,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task ClearAgentDefinitionAsync(Guid agentId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.AgentDefinitions
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);
        if (row is not null)
        {
            db.AgentDefinitions.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SeedAgentParentMembershipAsync(
        Guid agentId,
        Guid unitId,
        CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Units.IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new Cvoya.Spring.Core.Units.UnitMembership(unitId, agentId, Enabled: true),
            ct);
    }

    private async Task ClearMembershipsAsync(CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitMemberships.RemoveRange(db.UnitMemberships.ToList());
        await db.SaveChangesAsync(ct);
    }

    private void ArrangeAgentDirectoryEntry(Guid agentId, string displayName)
    {
        var entry = new DirectoryEntry(
            new Address("agent", agentId),
            agentId,
            displayName,
            displayName,
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);
    }

    private IAgentActor ArrangeAgentRuntimeStatus(Guid agentId, int inFlight, int queued, int channels)
    {
        var actorIdString = agentId.ToString("N");
        var proxy = Substitute.For<IAgentActor>();
        proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport(
                InFlightThreadCount: inFlight,
                QueuedMessageCount: queued,
                ChannelCount: channels,
                ObservedAt: DateTimeOffset.UtcNow));
        _factory.ActorProxyFactory
            .CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorIdString),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private void ArrangeAgentExecutionShape(Guid agentId, string hosting)
    {
        var actorIdString = agentId.ToString("N");
        _factory.AgentExecutionStore
            .GetAsync(actorIdString, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(Hosting: hosting)));
    }

    [Fact]
    public async Task DeleteAgent_UndeploysPersistentRuntimeBeforeUnregister()
    {
        // #2649: DELETE /agents/{id} must drop any persistent runtime
        // (container + per-agent workspace volume that still holds the
        // agent's live credentials) before unregistering the directory
        // entry. The gateway is idempotent: a no-op for ephemeral agents
        // or for persistent agents that were never deployed, so this
        // fires unconditionally.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var actorIdString = agentId.ToString("N");

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ExecutionHostGateway.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", agentId),
                agentId,
                "Agent",
                "Some agent",
                null,
                DateTimeOffset.UtcNow));

        var response = await _client.DeleteAsync($"/api/v1/tenant/agents/{actorIdString}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.ExecutionHostGateway.Received(1)
            .UndeployAsync(actorIdString, Arg.Any<CancellationToken>());
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAgent_UndeployFails_StillUnregisters()
    {
        // The undeploy step is best-effort, mirroring the unit
        // teardown pattern (#2397/#2708) — a worker failure is logged
        // and swallowed so the agent still disappears from the
        // directory. The operator's recovery path for a leaked
        // container is the runtime's own cleanup tools.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var actorIdString = agentId.ToString("N");

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ExecutionHostGateway.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", agentId),
                agentId,
                "Agent",
                "Some agent",
                null,
                DateTimeOffset.UtcNow));
        _factory.ExecutionHostGateway
            .UndeployAsync(actorIdString, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("worker offline"));

        var response = await _client.DeleteAsync($"/api/v1/tenant/agents/{actorIdString}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId),
            Arg.Any<CancellationToken>());
    }
}
