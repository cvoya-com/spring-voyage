// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
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
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

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
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Running);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Address("agent", "alpha"),
                new Address("agent", "beta"),
                new Address("unit", "child"),
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
        details.GetProperty("Status").GetString().ShouldBe(nameof(UnitStatus.Running));
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
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
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
        // GetStatusAsync is called twice: once by TryGetUnitStatusAsync for
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
            .Returns<UnitStatus>(_ => throw new InvalidOperationException("actor down"));
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
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Error);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());

        ArrangeResolved(proxy);

        // Seed the UnitDefinitionEntity row with a validation failure so
        // the GET endpoint's TryGetValidationTrackingAsync helper picks up
        // both columns.
        var error = new UnitValidationError(
            UnitValidationStep.ValidatingCredential,
            UnitValidationCodes.CredentialInvalid,
            Message: "credential rejected",
            Details: new Dictionary<string, string> { ["http_status"] = "401" });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                TenantId = "default",
                UnitId = UnitName,
                ActorId = ActorId,
                Name = UnitName,
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
        lastError.GetProperty("code").GetString().ShouldBe(UnitValidationCodes.CredentialInvalid);
        lastError.GetProperty("message").GetString().ShouldBe("credential rejected");
        lastError.GetProperty("details").GetProperty("http_status").GetString().ShouldBe("401");

        // Cleanup so later Theory rows don't see the seeded row. The
        // in-memory provider's DB is per-fixture but rows persist across
        // tests — clear our unit.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var row = await db.UnitDefinitions.FirstOrDefaultAsync(
                u => u.UnitId == UnitName, ct);
            if (row is not null)
            {
                db.UnitDefinitions.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    [Fact]
    public async Task GetUnit_NoValidationTracking_DtoFieldsAreNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
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
            new Address("unit", UnitName),
            ActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName), Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Is<ActorId>(a => a.GetId() == ActorId), Arg.Any<string>())
            .Returns(proxy);
    }
}