// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the unified unit-policy endpoints introduced by
/// #162: <c>GET /api/v1/units/{id}/policy</c> and
/// <c>PUT /api/v1/units/{id}/policy</c>. Covers CRUD round-trip, the default
/// empty-policy return for units that never set a policy, and 404 for
/// unknown unit ids.
/// </summary>
/// <remarks>
/// <para>
/// Each test uses a unique unit id (suffixed with a fresh GUID) to keep
/// state isolated. The class fixture shares a single
/// <see cref="CustomWebApplicationFactory"/> — and therefore a single
/// in-memory <c>SpringDbContext</c> — across all tests, so a shared
/// literal unit name would let a row written by one test leak into the
/// "no policy persisted" test and fail it non-deterministically. See #256.
/// </para>
/// </remarks>
public class UnitPolicyEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitPolicyEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetPolicy_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // The UnitViewer gate runs before the handler; arrange a permissive
        // grant so the test observes the handler's 404 (the declared
        // behaviour under test) rather than the gate's 403.
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, "ghost", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Owner);

        var response = await _client.GetAsync($"/api/v1/tenant/units/ghost/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPolicy_NoPolicyPersisted_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitName}/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitPolicyResponse>(
            cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.Skill.ShouldBeNull();
    }

    [Fact]
    public async Task PutPolicy_PersistsAndGetReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var putBody = new UnitPolicyResponse(
            new SkillPolicy(
                Allowed: new[] { "search", "summarize" },
                Blocked: new[] { "delete_repo" }));

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy", putBody, ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitName}/policy", ct);
        getResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await getResponse.Content.ReadFromJsonAsync<UnitPolicyResponse>(
            cancellationToken: ct);
        stored.ShouldNotBeNull();
        stored!.Skill.ShouldNotBeNull();
        stored.Skill!.Allowed.ShouldBe(new[] { "search", "summarize" });
        stored.Skill.Blocked.ShouldBe(new[] { "delete_repo" });
    }

    [Fact]
    public async Task PutPolicy_Overwrite_ReplacesExisting()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "old" })),
            ct);

        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "new" })),
            ct);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/tenant/units/{unitName}/policy", ct);
        stored!.Skill!.Blocked.ShouldBe(new[] { "new" });
    }

    [Fact]
    public async Task PutPolicy_EmptyPolicy_ClearsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        var clearResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(null),
            ct);
        clearResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/tenant/units/{unitName}/policy", ct);
        stored!.Skill.ShouldBeNull();
    }

    [Fact]
    public async Task PutPolicy_AllDimensions_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var putBody = new UnitPolicyResponse(
            Skill: new SkillPolicy(Allowed: new[] { "search" }),
            Model: new ModelPolicy(Blocked: new[] { "gpt-4" }),
            Cost: new CostPolicy(MaxCostPerInvocation: 0.25m, MaxCostPerHour: 5m, MaxCostPerDay: 50m),
            ExecutionMode: new ExecutionModePolicy(Forced: AgentExecutionMode.OnDemand),
            Initiative: new InitiativePolicy(BlockedActions: new[] { "delete-repo" }));

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy", putBody, WireJson, ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/tenant/units/{unitName}/policy", WireJson, ct);
        stored.ShouldNotBeNull();
        stored!.Skill!.Allowed.ShouldBe(new[] { "search" });
        stored.Model!.Blocked.ShouldBe(new[] { "gpt-4" });
        stored.Cost!.MaxCostPerInvocation.ShouldBe(0.25m);
        stored.Cost.MaxCostPerHour.ShouldBe(5m);
        stored.Cost.MaxCostPerDay.ShouldBe(50m);
        stored.ExecutionMode!.Forced.ShouldBe(AgentExecutionMode.OnDemand);
        stored.Initiative!.BlockedActions.ShouldBe(new[] { "delete-repo" });
    }

    [Fact]
    public async Task PutPolicy_ModelOnly_SkillRemainsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var putBody = new UnitPolicyResponse(
            Model: new ModelPolicy(Blocked: new[] { "gpt-4" }));

        await _client.PutAsJsonAsync($"/api/v1/tenant/units/{unitName}/policy", putBody, ct);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/tenant/units/{unitName}/policy", ct);
        stored!.Skill.ShouldBeNull();
        stored.Model!.Blocked.ShouldBe(new[] { "gpt-4" });
        stored.Cost.ShouldBeNull();
        stored.ExecutionMode.ShouldBeNull();
        stored.Initiative.ShouldBeNull();
    }

    [Fact]
    public async Task PutPolicy_ClearOneDimension_OthersPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(
                Skill: new SkillPolicy(Blocked: new[] { "dangerous" }),
                Model: new ModelPolicy(Blocked: new[] { "gpt-4" })),
            ct);

        // Clear model but keep skill.
        await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/policy",
            new UnitPolicyResponse(
                Skill: new SkillPolicy(Blocked: new[] { "dangerous" })),
            ct);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/tenant/units/{unitName}/policy", ct);
        stored!.Skill!.Blocked.ShouldBe(new[] { "dangerous" });
        stored.Model.ShouldBeNull();
    }

    [Fact]
    public async Task PutPolicy_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // The UnitOwner gate runs before the handler; arrange a permissive
        // grant so the test observes the handler's 404 (the declared
        // behaviour under test) rather than the gate's 403.
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, "ghost", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Owner);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/ghost/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string NewUnitName() => $"engineering-{Guid.NewGuid():N}";

    /// <summary>
    /// JSON options matching the host's wire format: enums serialize as their
    /// string names. The default <see cref="HttpClientJsonExtensions"/>
    /// options serialize enums as integers, which the host rejects under
    /// <c>JsonStringEnumConverter(allowIntegerValues: false)</c>.
    /// </summary>
    private static readonly JsonSerializerOptions WireJson = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void ArrangeResolved(string unitName)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == unitName),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address("unit", unitName),
                $"actor-{unitName}",
                "Engineering",
                "Engineering unit",
                null,
                DateTimeOffset.UtcNow));

        // The endpoints are gated by UnitOwner / UnitViewer policies (#1001).
        // The happy-path tests in this file write then read the policy, so
        // arrange Owner on the LocalDev caller so both verbs are allowed.
        _factory.PermissionService
            .ResolveEffectivePermissionAsync(
                AuthConstants.DefaultLocalUserId, unitName, Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Owner);
    }
}