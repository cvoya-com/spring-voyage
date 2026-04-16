// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitMembershipBackfillService"/>
/// (#160 / C2b-1, resilience fix #385). Verifies that every agent with a
/// legacy ParentUnit state produces a membership row, that repeat runs
/// are idempotent, and that failures never crash the host.
/// </summary>
public class UnitMembershipBackfillServiceTests
{
    [Fact]
    public async Task RunBackfillAsync_BackfillDisabled_DoesNothing()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateService(ctx, directory, proxyFactory, enabled: false);

        await service.StartAndWaitAsync(TestContext.Current.CancellationToken);

        await directory.DidNotReceive().ListAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunBackfillAsync_CreatesMembershipPerAgentWithParentUnit()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();

        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", "ada"), "actor-ada", "ada", "desc", null, DateTimeOffset.UtcNow),
                new(new Address("agent", "hopper"), "actor-hopper", "hopper", "desc", null, DateTimeOffset.UtcNow),
                new(new Address("unit", "engineering"), "actor-eng", "eng", "desc", null, DateTimeOffset.UtcNow),
            });

        StubAgentMetadata(proxyFactory, "actor-ada", new AgentMetadata(ParentUnit: "engineering"));
        StubAgentMetadata(proxyFactory, "actor-hopper", new AgentMetadata(ParentUnit: "marketing"));

        var service = CreateService(ctx, directory, proxyFactory);
        await service.RunBackfillAsync(TestContext.Current.CancellationToken);

        var repo = new UnitMembershipRepository(ctx);
        (await repo.GetAsync("engineering", "ada", CancellationToken.None)).ShouldNotBeNull();
        (await repo.GetAsync("marketing", "hopper", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task RunBackfillAsync_SkipsAgentsWithoutParentUnit()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();

        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", "loner"), "actor-loner", "loner", "desc", null, DateTimeOffset.UtcNow),
            });

        StubAgentMetadata(proxyFactory, "actor-loner", new AgentMetadata());

        var service = CreateService(ctx, directory, proxyFactory);
        await service.RunBackfillAsync(TestContext.Current.CancellationToken);

        var repo = new UnitMembershipRepository(ctx);
        (await repo.ListByAgentAsync("loner", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RunBackfillAsync_Idempotent_DoesNotOverwriteExistingRow()
    {
        var ctx = CreateContext();
        var repo = new UnitMembershipRepository(ctx);
        // Pre-seed a membership with a per-membership override that must survive.
        await repo.UpsertAsync(
            new UnitMembership("engineering", "ada",
                Model: "custom-model",
                Specialty: "reviewer",
                Enabled: false),
            CancellationToken.None);

        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", "ada"), "actor-ada", "ada", "desc", null, DateTimeOffset.UtcNow),
            });
        StubAgentMetadata(proxyFactory, "actor-ada", new AgentMetadata(ParentUnit: "engineering"));

        var service = CreateService(ctx, directory, proxyFactory);
        await service.RunBackfillAsync(TestContext.Current.CancellationToken);

        var persisted = await repo.GetAsync("engineering", "ada", CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted!.Model.ShouldBe("custom-model");
        persisted.Specialty.ShouldBe("reviewer");
        persisted.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task RunBackfillAsync_ActorCallThrows_LogsWarningAndContinues()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();

        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", "failing"), "actor-failing", "failing", "desc", null, DateTimeOffset.UtcNow),
                new(new Address("agent", "ok"), "actor-ok", "ok", "desc", null, DateTimeOffset.UtcNow),
            });

        // First agent throws (simulates sidecar not ready)
        var failingProxy = Substitute.For<IAgentActor>();
        failingProxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("sidecar unavailable"));
        proxyFactory.CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(a => a.GetId() == "actor-failing"),
                Arg.Any<string>())
            .Returns(failingProxy);

        // Second agent succeeds
        StubAgentMetadata(proxyFactory, "actor-ok", new AgentMetadata(ParentUnit: "engineering"));

        var service = CreateService(ctx, directory, proxyFactory);

        // Should NOT throw — the service continues past the failing agent
        await service.RunBackfillAsync(TestContext.Current.CancellationToken);

        var repo = new UnitMembershipRepository(ctx);
        (await repo.GetAsync("engineering", "ok", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryThrows_RetriesAndCompletes()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var callCount = 0;

        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("sidecar not ready");
                }

                return Task.FromResult<IReadOnlyList<DirectoryEntry>>(
                    new List<DirectoryEntry>
                    {
                        new(new Address("agent", "ada"), "actor-ada", "ada", "desc", null, DateTimeOffset.UtcNow),
                    });
            });

        StubAgentMetadata(proxyFactory, "actor-ada", new AgentMetadata(ParentUnit: "engineering"));

        var service = CreateService(ctx, directory, proxyFactory,
            initialDelay: TimeSpan.Zero, retryDelay: TimeSpan.Zero);

        await service.StartAndWaitAsync(TestContext.Current.CancellationToken);

        callCount.ShouldBe(2);
        var repo = new UnitMembershipRepository(ctx);
        (await repo.GetAsync("engineering", "ada", CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_AllAttemptsExhausted_DoesNotThrow()
    {
        var ctx = CreateContext();
        var directory = Substitute.For<IDirectoryService>();
        var proxyFactory = Substitute.For<IActorProxyFactory>();

        directory.ListAllAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("sidecar permanently down"));

        var service = CreateService(ctx, directory, proxyFactory,
            initialDelay: TimeSpan.Zero, retryDelay: TimeSpan.Zero);

        // Must NOT throw — the host must stay alive.
        await service.StartAndWaitAsync(TestContext.Current.CancellationToken);

        await directory.Received(UnitMembershipBackfillService.MaxAttempts)
            .ListAllAsync(Arg.Any<CancellationToken>());
    }

    private static SpringDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SpringDbContext(options);
    }

    private static void StubAgentMetadata(
        IActorProxyFactory factory, string actorId, AgentMetadata metadata)
    {
        var proxy = Substitute.For<IAgentActor>();
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>()).Returns(metadata);
        factory.CreateActorProxy<IAgentActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private static TestableBackfillService CreateService(
        SpringDbContext ctx,
        IDirectoryService directory,
        IActorProxyFactory proxyFactory,
        bool enabled = true,
        TimeSpan? initialDelay = null,
        TimeSpan? retryDelay = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUnitMembershipRepository>(_ => new UnitMembershipRepository(ctx));
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new DatabaseOptions { BackfillMemberships = enabled });
        return new TestableBackfillService(
            provider, directory, proxyFactory, options,
            Substitute.For<ILogger<UnitMembershipBackfillService>>(),
            initialDelay ?? TimeSpan.Zero,
            retryDelay ?? TimeSpan.Zero);
    }

    /// <summary>
    /// Test subclass that overrides the delays to zero for fast tests
    /// and provides a helper to run <c>ExecuteAsync</c> to completion.
    /// </summary>
    private sealed class TestableBackfillService(
        IServiceProvider services,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IOptions<DatabaseOptions> options,
        ILogger<UnitMembershipBackfillService> logger,
        TimeSpan initialDelay,
        TimeSpan retryDelay)
        : UnitMembershipBackfillService(services, directoryService, actorProxyFactory, options, logger)
    {
        private readonly TimeSpan _initialDelay = initialDelay;
        private readonly TimeSpan _retryDelay = retryDelay;

        /// <summary>
        /// Starts the background service and waits for <c>ExecuteAsync</c>
        /// to complete (or fail). In tests, the delays are zero so this
        /// returns quickly.
        /// </summary>
        public async Task StartAndWaitAsync(CancellationToken ct)
        {
            await StartAsync(ct);

            // BackgroundService.StartAsync stores the task returned by
            // ExecuteAsync in the ExecuteTask property.
            if (ExecuteTask is not null)
            {
                await ExecuteTask;
            }
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Replicate the base logic but with overridable delays.
            if (!Options.BackfillMemberships)
            {
                return;
            }

            await Task.Delay(_initialDelay, stoppingToken);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await RunBackfillAsync(stoppingToken);
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (attempt < MaxAttempts)
                    {
                        await Task.Delay(_retryDelay, stoppingToken);
                    }
                }
            }
        }

        private DatabaseOptions Options { get; } = options.Value;
    }
}