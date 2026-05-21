// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers <see cref="MessageDeliveryService"/> — the shared delivery seam:
/// the per-thread hop counter (#2576), the cross-tenant containment gate,
/// and the bounded-retry delivery loop (ADR-0049 §4).
/// </summary>
public class MessageDeliveryServiceTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OtherTenantId = new("ffffffff-0000-0000-0000-000000000099");

    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IOrchestrationTenantResolver _tenantResolver = Substitute.For<IOrchestrationTenantResolver>();
    private readonly ILogger<MessageDeliveryService> _logger =
        Substitute.For<ILogger<MessageDeliveryService>>();

    public MessageDeliveryServiceTests()
    {
        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);
    }

    [Fact]
    public async Task EnsureHopBudget_UnderLimit_DoesNotThrow()
    {
        var hopActor = StubHopActor(1, 2, 3);
        var service = CreateService(maxHopCount: 16);

        // Three hops on the same thread, all under the limit.
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);

        await hopActor.Received(3).IncrementAsync();
    }

    [Fact]
    public async Task EnsureHopBudget_ExceedsLimit_ThrowsDepthExceeded()
    {
        // The hop actor reports a count past the limit — the cycle terminates.
        StubHopActor(17);
        var service = CreateService(maxHopCount: 16);

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationDepthExceeded);
    }

    [Fact]
    public async Task EnsureHopBudget_AtLimit_DoesNotThrow()
    {
        // Hop count exactly at the limit is still permitted; only a count
        // strictly past it is rejected.
        StubHopActor(16);
        var service = CreateService(maxHopCount: 16);

        await Should.NotThrowAsync(() =>
            service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public void EnsureNotSelfTarget_SameAddress_ThrowsSelfDelegation()
    {
        var address = new Address(Address.UnitScheme, Guid.NewGuid());

        var ex = Should.Throw<OrchestrationException>(() =>
            MessageDeliveryService.EnsureNotSelfTarget(address, address));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task EnsureCallerTenant_Mismatch_ThrowsCrossTenant()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);
        var service = CreateService();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            service.EnsureCallerTenantAsync(caller, TenantId, CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationCrossTenant);
    }

    [Fact]
    public async Task DeliverWithRetry_TransientThenSuccess_DeliversWithinBudget()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var attempts = 0;

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return attempts < 2
                    ? throw new InvalidOperationException("transient dapr hiccup")
                    : Task.FromResult<Message?>(null);
            });

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task DeliverWithRetry_ExhaustsBudget_ThrowsDeliveryFailed()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException("dapr is down"));

        var service = CreateService();
        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationDeliveryFailed);
    }

    private IThreadHopActor StubHopActor(params int[] returnsSequence)
    {
        var hopActor = Substitute.For<IThreadHopActor>();
        var queue = new Queue<int>(returnsSequence);
        hopActor.IncrementAsync().Returns(_ => queue.Count > 0 ? queue.Dequeue() : returnsSequence[^1]);
        _actorProxyFactory
            .CreateActorProxy<IThreadHopActor>(Arg.Any<ActorId>(), nameof(ThreadHopActor))
            .Returns(hopActor);
        return hopActor;
    }

    private MessageDeliveryService CreateService(int maxHopCount = 16) =>
        new(
            _agentProxyResolver,
            _actorProxyFactory,
            _tenantResolver,
            _logger,
            Options.Create(new OrchestrationDeliveryOptions
            {
                MaxAttempts = 3,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                MaxHopCount = maxHopCount,
            }));

    private static Message CreateMessage() =>
        new(
            Guid.NewGuid(),
            new Address(Address.UnitScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "work" }),
            DateTimeOffset.UtcNow);
}
