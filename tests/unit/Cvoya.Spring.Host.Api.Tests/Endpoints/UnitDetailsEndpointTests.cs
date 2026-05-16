// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for issue #339: <c>GET /api/v1/units/{id}</c> must return
/// a non-null <c>details</c> payload regardless of the authenticated caller,
/// because the endpoint is a platform-internal read that must not be gated
/// by the router's unit-permission check (that gate exists to protect
/// external-dispatch from unauthenticated senders). Pre-fix the endpoint
/// synthesised <c>human://api</c> as the From on a status-query routed
/// through <see cref="Dapr.Routing.MessageRouter"/>, which since #328 has
/// no Viewer permission on units whose creator is the authenticated
/// subject (e.g. <c>local-dev-user</c> in LocalDev mode).
/// </summary>
public class UnitDetailsEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_Alpha_Id = new("00000001-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_Beta_Id = new("00000002-1234-5678-9abc-000000000000");
    private static readonly Guid Unit_Child_Id = new("00000003-1234-5678-9abc-000000000000");
    private static readonly Guid ActorEngineering_Id = new("00000004-1234-5678-9abc-000000000000");

    private const string UnitDisplayName = "engineering";
    private static readonly Guid ActorId_Guid = ActorEngineering_Id;
    private static readonly string ActorId = ActorId_Guid.ToString("N");
    // Post-#1629 URL paths carry the unit's Guid hex.
    private static readonly string UnitName = ActorId;

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitDetailsEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetUnit_AuthenticatedCaller_ReturnsDetailsFromActorProxy()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Running);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Address("agent", Agent_Alpha_Id),
                new Address("agent", Agent_Beta_Id),
                new Address("unit", Unit_Child_Id),
            });

        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("details", out var details).ShouldBeTrue();
        details.ValueKind.ShouldBe(JsonValueKind.Object);

        // Pre-fix this would be null because MessageRouter denied the
        // hardcoded human://api sender on a unit owned by 'local-dev-user'.
        // The raw payload shape is PascalCase — ConfigureHttpJsonOptions
        // only re-serialises top-level response DTOs; a JsonElement
        // (UnitDetailResponse.Details) is written through as-is.
        details.GetProperty("Status").GetString().ShouldBe(nameof(LifecycleStatus.Running));
        details.GetProperty("MemberCount").GetInt32().ShouldBe(3);

        // #339: the status-query payload now also carries the full member
        // list so the web UI and e2e/12-nested-units scenario can verify
        // containment without issuing a second round-trip.
        details.GetProperty("Members").GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public async Task GetUnit_DoesNotDispatchThroughMessageRouter()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Regression anchor: the endpoint must read state directly through
        // the actor proxy. It must NOT construct a Message or hand it to
        // IAgentProxyResolver (the resolver is MessageRouter's delivery
        // surface — zero calls on it during GET proves the router is out of
        // the path, even without an IMessageRouter mock).
        _factory.AgentProxyResolver.DidNotReceiveWithAnyArgs().Resolve(default!, default!);

        // And the direct-proxy calls must have happened.
        // GetStatusAsync is called twice: once by TryGetLifecycleStatusAsync for
        // the top-level UnitResponse.Status projection (pre-existing) and
        // once by the #339 status-payload helper. That's deliberate — the
        // two helpers serve different fields. A single call would be a bug:
        // the top-level Status would go stale if the two reads diverged.
        await proxy.Received(2).GetStatusAsync(Arg.Any<CancellationToken>());
        await proxy.Received(1).GetMembersAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUnit_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "ghost"), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/units/ghost", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUnit_ActorProxyThrows_FallsBackToNullDetailsButKeepsEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        // The helper that reads metadata/status swallows exceptions and
        // surfaces Draft + empty metadata so the envelope still renders;
        // the new status-payload helper mirrors that resilience by returning
        // null details on any proxy failure.
        proxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns<LifecycleStatus>(_ => throw new InvalidOperationException("actor down"));
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns<UnitMetadata>(_ => throw new InvalidOperationException("actor down"));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns<Address[]>(_ => throw new InvalidOperationException("actor down"));

        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("details").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetUnit_WithValidationTracking_ProjectsErrorAndRunIdOntoResponse()
    {
        // T-05 (#947): the top-level DTO gains LastValidationError /
        // LastValidationRunId. The GET read-path reads the columns back
        // from UnitDefinitionEntity and projects them into UnitResponse.
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Error);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        // Seed the UnitDefinitionEntity row with a validation failure so
        // the GET endpoint's TryGetValidationTrackingAsync helper picks up
        // both columns.
        var error = new ArtefactValidationError(
            ArtefactValidationStep.ValidatingCredential,
            ArtefactValidationCodes.CredentialInvalid,
            Message: "credential rejected",
            Details: new Dictionary<string, string> { ["http_status"] = "401" });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = ActorEngineering_Id,
                TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
                DisplayName = UnitDisplayName,
                Description = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                LastValidationErrorJson = JsonSerializer.Serialize(error),
                LastValidationRunId = "run-17",
            });
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var unit = doc.RootElement.GetProperty("unit");
        unit.GetProperty("lastValidationRunId").GetString().ShouldBe("run-17");

        var lastError = unit.GetProperty("lastValidationError");
        lastError.GetProperty("step").GetString().ShouldBe("ValidatingCredential");
        lastError.GetProperty("code").GetString().ShouldBe(ArtefactValidationCodes.CredentialInvalid);
        lastError.GetProperty("message").GetString().ShouldBe("credential rejected");
        lastError.GetProperty("details").GetProperty("http_status").GetString().ShouldBe("401");

        // Cleanup so later Theory rows don't see the seeded row. The
        // in-memory provider's DB is per-fixture but rows persist across
        // tests — clear our unit.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var row = await db.UnitDefinitions.FirstOrDefaultAsync(
                u => u.DisplayName == UnitDisplayName, ct);
            if (row is not null)
            {
                db.UnitDefinitions.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // -------------------------------------------------------------------
    // GET /api/v1/tenant/units/{id} — effective-tools projection (#2337 Sub D).
    //
    // The Show endpoint projects IToolGrantResolver.ResolveAsync into the
    // wire response so the portal's Tools sub-tab can render the three-
    // tier layout (platform / connector / image) without re-deriving the
    // grant set. The factory's substitute resolver returns an empty list
    // by default; this test arranges a populated list and asserts the
    // projection lands on UnitDetailResponse.Unit.EffectiveTools.
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetUnit_EffectiveTools_PopulatedFromResolver()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Running);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        _factory.ToolGrantResolver
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.UnitScheme && a.Id == ActorEngineering_Id),
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
                        InheritedFromUnitName: null),
                }));

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var unit = doc.RootElement.GetProperty("unit");
        var effectiveTools = unit.GetProperty("effectiveTools");
        effectiveTools.ValueKind.ShouldBe(JsonValueKind.Array);
        effectiveTools.GetArrayLength().ShouldBe(2);

        var first = effectiveTools[0];
        first.GetProperty("name").GetString().ShouldBe("sv.expertise.lookup");
        first.GetProperty("namespace").GetString().ShouldBe("sv");
        first.GetProperty("provenance").GetString().ShouldBe("platform");
        first.GetProperty("inheritedFromUnitName").ValueKind.ShouldBe(JsonValueKind.Null);

        var second = effectiveTools[1];
        second.GetProperty("name").GetString().ShouldBe("github.create_issue");
        second.GetProperty("namespace").GetString().ShouldBe("github");
        second.GetProperty("provenance").GetString().ShouldBe("connector:github");
    }

    // -------------------------------------------------------------------
    // GET /api/v1/tenant/units/{id} — execution.image tag projection (#2348).
    //
    // The Show endpoint reads the unit's `execution.image` slot through
    // IAgentDefinitionProvider (the same path the dispatcher uses) and
    // surfaces the tag on UnitResponse.ExecutionImage so the portal's
    // Tools sub-tab Image section can render the tag rather than the
    // digest-suffixed provenance string. Both populated + null paths are
    // exercised; a malformed definition surfaces as null.
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetUnit_ExecutionImage_PopulatedFromDefinitionJson()
    {
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Running);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        // Seed the UnitDefinitionEntity with an execution block carrying
        // both the runtime id and the image tag — IAgentDefinitionProvider
        // requires `execution.agent` to project the block (see #1732 /
        // DbAgentDefinitionProvider.ExtractExecution).
        var definitionJson = JsonSerializer.SerializeToElement(new
        {
            execution = new
            {
                agent = "claude",
                image = "acme/agent:v1.2",
            },
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = ActorEngineering_Id,
                TenantId = Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
                DisplayName = UnitDisplayName,
                Description = "test",
                Definition = definitionJson,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        try
        {
            var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            var unit = doc.RootElement.GetProperty("unit");
            unit.GetProperty("executionImage").GetString().ShouldBe("acme/agent:v1.2");
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var row = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.Id == ActorEngineering_Id, ct);
            if (row is not null)
            {
                db.UnitDefinitions.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    [Fact]
    public async Task GetUnit_ExecutionImage_NullWhenDefinitionMissing()
    {
        // No UnitDefinitionEntity seeded — the provider returns null and
        // the wire field collapses to JSON null. The unit envelope still
        // renders so the Show endpoint stays usable while the operator
        // fills the missing execution block.
        var ct = TestContext.Current.CancellationToken;

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var unit = doc.RootElement.GetProperty("unit");
        unit.GetProperty("executionImage").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetUnit_NoValidationTracking_DtoFieldsAreNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Draft);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{UnitName}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        var unit = doc.RootElement.GetProperty("unit");
        unit.GetProperty("lastValidationError").ValueKind.ShouldBe(JsonValueKind.Null);
        unit.GetProperty("lastValidationRunId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.AgentProxyResolver.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("unit", ActorId_Guid),
            ActorId_Guid,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ActorId_Guid), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}
