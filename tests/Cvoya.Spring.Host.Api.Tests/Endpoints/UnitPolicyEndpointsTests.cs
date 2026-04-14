// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
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
public class UnitPolicyEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

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

        var response = await _client.GetAsync($"/api/v1/units/ghost/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPolicy_NoPolicyPersisted_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeResolved();

        var response = await _client.GetAsync($"/api/v1/units/{UnitName}/policy", ct);

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
        ArrangeResolved();

        var putBody = new UnitPolicyResponse(
            new SkillPolicy(
                Allowed: new[] { "search", "summarize" },
                Blocked: new[] { "delete_repo" }));

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/units/{UnitName}/policy", putBody, ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync(
            $"/api/v1/units/{UnitName}/policy", ct);
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
        ArrangeResolved();

        await _client.PutAsJsonAsync(
            $"/api/v1/units/{UnitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "old" })),
            ct);

        await _client.PutAsJsonAsync(
            $"/api/v1/units/{UnitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "new" })),
            ct);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/units/{UnitName}/policy", ct);
        stored!.Skill!.Blocked.ShouldBe(new[] { "new" });
    }

    [Fact]
    public async Task PutPolicy_EmptyPolicy_ClearsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeResolved();

        await _client.PutAsJsonAsync(
            $"/api/v1/units/{UnitName}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        var clearResponse = await _client.PutAsJsonAsync(
            $"/api/v1/units/{UnitName}/policy",
            new UnitPolicyResponse(null),
            ct);
        clearResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await _client
            .GetFromJsonAsync<UnitPolicyResponse>($"/api/v1/units/{UnitName}/policy", ct);
        stored!.Skill.ShouldBeNull();
    }

    [Fact]
    public async Task PutPolicy_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/units/ghost/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "x" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeResolved()
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address("unit", UnitName),
                ActorId,
                "Engineering",
                "Engineering unit",
                null,
                DateTimeOffset.UtcNow));
    }
}