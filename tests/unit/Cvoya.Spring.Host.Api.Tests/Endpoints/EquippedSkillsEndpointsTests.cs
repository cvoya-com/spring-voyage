// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint tests for the operator-equip surface introduced in #2360.
/// Three verbs × two subjects:
/// <list type="bullet">
///   <item><description><c>GET / POST / DELETE /api/v1/tenant/units/{id}/skills</c></description></item>
///   <item><description><c>GET / POST / DELETE /api/v1/tenant/agents/{id}/skills</c></description></item>
/// </list>
/// The store layer is faked through the
/// <see cref="CustomWebApplicationFactory"/> so these tests exercise
/// the endpoint wiring + projection only.
/// </summary>
public class EquippedSkillsEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new { });

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public EquippedSkillsEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        ResetStores();
    }

    private void ResetStores()
    {
        _factory.UnitSkillBundleStore.ClearReceivedCalls();
        _factory.AgentSkillBundleStore.ClearReceivedCalls();
        _factory.UnitSkillBundleStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(Array.Empty<SkillBundle>()));
        _factory.AgentSkillBundleStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(Array.Empty<SkillBundle>()));
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void ArrangeDirectoryHit(string scheme, Guid actorId)
    {
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == scheme && a.Path == actorId.ToString("N")), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address: Address.For(scheme, actorId.ToString("N")),
                ActorId: actorId,
                DisplayName: $"{scheme}-display",
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));
    }

    private void ArrangeDirectoryMiss(string scheme)
    {
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == scheme), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
    }

    private static SkillBundle MakeBundle(string pkg, string skill, string? prompt = null) =>
        new(
            PackageName: pkg,
            SkillName: skill,
            Prompt: prompt ?? $"# {skill}\nBody for {pkg}/{skill}",
            RequiredTools: new[]
            {
                new SkillToolRequirement($"{pkg}.do_thing", "do a thing", EmptySchema, Optional: false),
            });

    // ── Unit subject ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetUnitSkills_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss("unit");

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{Guid.NewGuid():N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnitSkills_ReturnsEquippedListWithSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", unitId);
        _factory.UnitSkillBundleStore.GetAsync(unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(new[]
            {
                MakeBundle("spring-voyage/software-engineering", "triage-and-assign"),
            }));

        var response = await _client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EquippedSkillsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Skills.Count.ShouldBe(1);
        body.Skills[0].PackageName.ShouldBe("spring-voyage/software-engineering");
        body.Skills[0].SkillName.ShouldBe("triage-and-assign");
        body.Skills[0].PromptSummary.ShouldBe("# triage-and-assign");
        body.Skills[0].RequiredTools.Single().Name.ShouldBe("spring-voyage/software-engineering.do_thing");
    }

    [Fact]
    public async Task EquipUnitSkill_PostsValidPayload_CallsAddAndReturnsList()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", unitId);

        var refOk = new SkillBundleReference("pkg-x", "skill-x");
        _factory.UnitSkillBundleStore.AddAsync(unitId.ToString("N"), Arg.Any<SkillBundleReference>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(new[]
            {
                MakeBundle(refOk.Package, refOk.Skill),
            }));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/skills",
            new EquipSkillRequest(refOk.Package, refOk.Skill),
            JsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.UnitSkillBundleStore.Received(1)
            .AddAsync(unitId.ToString("N"),
                Arg.Is<SkillBundleReference>(r => r.Package == "pkg-x" && r.Skill == "skill-x"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EquipUnitSkill_MissingFields_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", unitId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/skills",
            new EquipSkillRequest(string.Empty, "skill"),
            JsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.UnitSkillBundleStore.DidNotReceiveWithAnyArgs()
            .AddAsync(Arg.Any<string>(), Arg.Any<SkillBundleReference>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EquipUnitSkill_UnknownBundle_MapsResolverFailureTo400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", unitId);
        _factory.UnitSkillBundleStore.AddAsync(Arg.Any<string>(), Arg.Any<SkillBundleReference>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SkillBundleNotFoundException("pkg", "skill", "no such skill"));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/skills",
            new EquipSkillRequest("pkg", "skill"),
            JsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnequipUnitSkill_DeletesAndReturnsList()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeDirectoryHit("unit", unitId);
        _factory.UnitSkillBundleStore.RemoveAsync(unitId.ToString("N"), "pkg-y", "skill-y", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(Array.Empty<SkillBundle>()));

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{unitId:N}/skills/pkg-y/skill-y", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.UnitSkillBundleStore.Received(1)
            .RemoveAsync(unitId.ToString("N"), "pkg-y", "skill-y", Arg.Any<CancellationToken>());
    }

    // ── Agent subject ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentSkills_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeDirectoryMiss("agent");

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{Guid.NewGuid():N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAgentSkills_ReturnsEquippedList()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", agentId);
        _factory.AgentSkillBundleStore.GetAsync(agentId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(new[]
            {
                MakeBundle("pkg", "alpha"),
                MakeBundle("pkg", "beta"),
            }));

        var response = await _client.GetAsync(
            $"/api/v1/tenant/agents/{agentId:N}/skills", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EquippedSkillsResponse>(JsonOptions, ct);
        body.ShouldNotBeNull();
        body!.Skills.Count.ShouldBe(2);
        // Order is preserved from the store.
        body.Skills[0].SkillName.ShouldBe("alpha");
        body.Skills[1].SkillName.ShouldBe("beta");
    }

    [Fact]
    public async Task EquipAgentSkill_CallsAdd()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", agentId);
        _factory.AgentSkillBundleStore.AddAsync(agentId.ToString("N"), Arg.Any<SkillBundleReference>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(new[]
            {
                MakeBundle("pkg-z", "skill-z"),
            }));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/tenant/agents/{agentId:N}/skills",
            new EquipSkillRequest("pkg-z", "skill-z"),
            JsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.AgentSkillBundleStore.Received(1).AddAsync(
            agentId.ToString("N"),
            Arg.Is<SkillBundleReference>(r => r.Package == "pkg-z" && r.Skill == "skill-z"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnequipAgentSkill_CallsRemove()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        ArrangeDirectoryHit("agent", agentId);
        _factory.AgentSkillBundleStore.RemoveAsync(agentId.ToString("N"), "pkg-q", "skill-q", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SkillBundle>>(Array.Empty<SkillBundle>()));

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/agents/{agentId:N}/skills/pkg-q/skill-q", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _factory.AgentSkillBundleStore.Received(1).RemoveAsync(
            agentId.ToString("N"), "pkg-q", "skill-q", Arg.Any<CancellationToken>());
    }
}
