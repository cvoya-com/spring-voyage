// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the API-boundary <see cref="Message.From"/> rewrite
/// introduced by ADR-0062 § 3 — the OSS operator's auth principal
/// (<c>tenant-user://</c>) is rewritten to the speaking-as Hat
/// (<c>human://</c>) before <see cref="Message.From"/> is set.
/// </summary>
public class MessageEndpointsFromRewriteTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid AgentImplicitFromId = new("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid AgentExplicitFromId = new("11111111-aaaa-0000-0000-000000000002");
    private static readonly Guid AgentUnboundFromId = new("11111111-aaaa-0000-0000-000000000003");
    private static readonly Guid AgentAuditFromId = new("11111111-aaaa-0000-0000-000000000004");
    private static readonly Guid AgentNoReachId = new("11111111-aaaa-0000-0000-000000000005");
    private static readonly Guid AgentExplicitUnreachId = new("11111111-aaaa-0000-0000-000000000006");

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MessageEndpointsFromRewriteTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_NoExplicitFrom_StampsPrimaryHumanIdsHat()
    {
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentImplicitFromId, out var observed);

        var primaryHumanId = await GetPrimaryHumanIdAsync();
        primaryHumanId.ShouldNotBe(Guid.Empty);

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentImplicitFromId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hello" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        observed().ShouldNotBeNull();
        var m = observed()!;
        m.From.Scheme.ShouldBe(Address.HumanScheme);
        m.From.Id.ShouldBe(primaryHumanId);
    }

    [Fact]
    public async Task SendMessage_ExplicitFrom_BoundHuman_StampsThatHuman()
    {
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentExplicitFromId, out var observed);

        // Seed a second Human bound to the operator and pass it as the
        // explicit From.
        var explicitHumanId = await SeedHumanBoundToOperatorAsync();

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentExplicitFromId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hi" }),
            From: explicitHumanId);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        observed().ShouldNotBeNull();
        observed()!.From.Scheme.ShouldBe(Address.HumanScheme);
        observed()!.From.Id.ShouldBe(explicitHumanId);
    }

    [Fact]
    public async Task SendMessage_ExplicitFrom_UnboundHuman_Returns400NoBoundHuman()
    {
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentUnboundFromId, out var _);

        // Plant a Human bound to a DIFFERENT TenantUser — the caller is
        // the operator, so this Human is unbound for them. Per ADR-0062
        // § 3 the explicit override must validate ∈ caller's bound set.
        var otherTenantUserId = Guid.Parse("ffffffff-aaaa-0000-0000-000000000099");
        var unboundHumanId = await SeedHumanBoundToAsync(otherTenantUserId);

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentUnboundFromId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hi" }),
            From: unboundHumanId);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe(
            Cvoya.Spring.Core.Messaging.ITenantUserHumanResolver.NoBoundHumanCode);
    }

    [Fact]
    public async Task SendMessage_EmitsOutboundAuditEnvelope_WithFromAndActingTenantUser()
    {
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentAuditFromId, out var _);

        ActivityEvent? captured = null;
        _factory.ActivityEventBus
            .When(b => b.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>()))
            .Do(call =>
            {
                var ev = call.Arg<ActivityEvent>();
                // Capture only the outbound-message envelope (skip any
                // other events the host may emit during the request).
                if (ev.EventType == ActivityEventType.MessageSent
                    && ev.Details is { } d
                    && d.TryGetProperty(OutboundMessageAuditEmitter.FromAddressProperty, out _))
                {
                    captured = ev;
                }
            });

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentAuditFromId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "audit-me" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        captured.ShouldNotBeNull();
        var details = captured!.Details!.Value;

        var fromAddress = details
            .GetProperty(OutboundMessageAuditEmitter.FromAddressProperty)
            .GetProperty("address")
            .GetString();
        fromAddress.ShouldStartWith($"{Address.HumanScheme}://");

        var actingTenantUserId = details
            .GetProperty(OutboundMessageAuditEmitter.ActingTenantUserIdProperty)
            .GetString();
        // ADR-0062 § 4: the auth principal travels alongside the routable
        // From so the cloud render can reconstruct the permission decision
        // from observation alone.
        actingTenantUserId.ShouldNotBeNull();
        actingTenantUserId.ShouldBe(
            $"{Address.TenantUserScheme}://{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(OssTenantUserIds.Operator)}");
    }

    [Fact]
    public async Task SendMessage_NoWearableHat_Returns403NoReachableHat()
    {
        // #2972: the Hat ↔ unit reachability gate — the operator wears no
        // Hat that can reach this agent, so the platform rejects the send.
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentNoReachId, out _);
        _factory.HatReachability.GetWearableHatsAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyCollection<Address>>(t => t.Any(a => a.Id == AgentNoReachId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>()));

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentNoReachId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hi" }));

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe(
            Cvoya.Spring.Core.Messaging.ITenantUserHumanResolver.NoReachableHatCode);
    }

    [Fact]
    public async Task SendMessage_ExplicitHatNotReachable_Returns403HatCannotReachTarget()
    {
        // #2972: the explicit --as Hat is bound to the operator but cannot
        // reach this target — a distinct rejection from "no wearable Hat".
        var ct = TestContext.Current.CancellationToken;
        StubAgent(AgentExplicitUnreachId, out _);
        var explicitHumanId = await GetPrimaryHumanIdAsync();
        var reachableButOtherHat = await SeedHumanBoundToOperatorAsync();

        // The wearable set for this target excludes the explicitly-chosen Hat.
        _factory.HatReachability.GetWearableHatsAsync(
                Arg.Any<Guid>(),
                Arg.Is<IReadOnlyCollection<Address>>(t => t.Any(a => a.Id == AgentExplicitUnreachId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(new[] { reachableButOtherHat }));

        var request = new SendMessageRequest(
            new AddressDto("agent", AgentExplicitUnreachId.ToString("N")),
            "Domain",
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Text = "hi" }),
            From: explicitHumanId);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/messages", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe(
            Cvoya.Spring.Core.Messaging.ITenantUserHumanResolver.HatCannotReachTargetCode);
    }

    private void StubAgent(Guid agentId, out Func<Message?> observed)
    {
        var entry = new DirectoryEntry(
            new Address("agent", agentId),
            agentId,
            $"agent-{agentId:N}",
            "stub",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == agentId),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        Message? captured = null;
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Do<Message>(m => captured = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _factory.AgentProxyResolver
            .Resolve(Arg.Is<string>(s => string.Equals(s, "agent", StringComparison.OrdinalIgnoreCase)),
                agentId.ToString("N"))
            .Returns(agent);

        observed = () => captured;
    }

    private async Task<Guid> GetPrimaryHumanIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var primary = await db.TenantUsers
            .Where(u => u.Id == OssTenantUserIds.Operator)
            .Select(u => u.PrimaryHumanId)
            .SingleAsync(TestContext.Current.CancellationToken);
        return primary ?? Guid.Empty;
    }

    private async Task<Guid> SeedHumanBoundToOperatorAsync()
        => await SeedHumanBoundToAsync(OssTenantUserIds.Operator);

    private async Task<Guid> SeedHumanBoundToAsync(Guid tenantUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        // For non-operator caller, ensure the TenantUser row exists too.
        if (tenantUserId != OssTenantUserIds.Operator
            && !await db.TenantUsers.AnyAsync(u => u.Id == tenantUserId, TestContext.Current.CancellationToken))
        {
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = tenantUserId,
                TenantId = OssTenantIds.Default,
                DisplayName = $"u-{tenantUserId:N}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var humanId = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = humanId,
            TenantId = OssTenantIds.Default,
            TenantUserId = tenantUserId,
            Username = $"h-{humanId:N}",
            DisplayName = $"h-{humanId:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return humanId;
    }
}
