// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DefaultTenantBootstrapService"/>. Covers the
/// gating flag, priority ordering, idempotency-friendly re-runs, the
/// audit-log bypass scope, and the fail-loud behaviour when a seed
/// provider throws.
/// </summary>
public class DefaultTenantBootstrapServiceTests
{
    [Fact]
    public async Task StartAsync_FlagDisabled_DoesNotInvokeProviders()
    {
        var provider = new RecordingSeedProvider("p1", priority: 0);
        var sut = CreateSut([provider], new TenancyOptions { BootstrapDefaultTenant = false });

        await sut.StartAsync(TestContext.Current.CancellationToken);

        provider.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_FlagEnabled_InvokesEveryProviderOnceWithDefaultTenant()
    {
        var p1 = new RecordingSeedProvider("p1", priority: 10);
        var p2 = new RecordingSeedProvider("p2", priority: 20);

        var sut = CreateSut([p1, p2], new TenancyOptions { BootstrapDefaultTenant = true });

        await sut.StartAsync(TestContext.Current.CancellationToken);

        p1.Calls.Count.ShouldBe(1);
        p1.Calls[0].ShouldBe("default");
        p2.Calls.Count.ShouldBe(1);
        p2.Calls[0].ShouldBe("default");
    }

    [Fact]
    public async Task StartAsync_OrdersProvidersByPriorityAscending()
    {
        var executionOrder = new List<string>();
        var p1 = new RecordingSeedProvider("late", priority: 200, onApply: id => executionOrder.Add("late"));
        var p2 = new RecordingSeedProvider("early", priority: 1, onApply: id => executionOrder.Add("early"));
        var p3 = new RecordingSeedProvider("middle", priority: 50, onApply: id => executionOrder.Add("middle"));

        var sut = CreateSut([p1, p2, p3], new TenancyOptions { BootstrapDefaultTenant = true });

        await sut.StartAsync(TestContext.Current.CancellationToken);

        executionOrder.ShouldBe(["early", "middle", "late"]);
    }

    [Fact]
    public async Task StartAsync_TiesBrokenByIdOrdinalAscending()
    {
        var executionOrder = new List<string>();
        var pBeta = new RecordingSeedProvider("beta", priority: 5, onApply: id => executionOrder.Add("beta"));
        var pAlpha = new RecordingSeedProvider("alpha", priority: 5, onApply: id => executionOrder.Add("alpha"));

        var sut = CreateSut([pBeta, pAlpha], new TenancyOptions { BootstrapDefaultTenant = true });

        await sut.StartAsync(TestContext.Current.CancellationToken);

        executionOrder.ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task StartAsync_NoProviders_StillRunsAndCompletes()
    {
        var sut = CreateSut([], new TenancyOptions { BootstrapDefaultTenant = true });

        await Should.NotThrowAsync(() => sut.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_RerunningOnSamePass_DelegatesToProviderIdempotency()
    {
        // The bootstrap service itself does not snapshot state — it just
        // calls providers. This test pins the contract: a second StartAsync
        // (e.g. host restart) calls every provider again, so providers
        // themselves must be idempotent. The seed-bundle provider is the
        // canonical OSS example; here we use a recording provider that
        // tracks call counts to verify the dispatch happens twice.
        var p = new RecordingSeedProvider("p", priority: 0);
        var sut = CreateSut([p], new TenancyOptions { BootstrapDefaultTenant = true });

        await sut.StartAsync(TestContext.Current.CancellationToken);
        await sut.StartAsync(TestContext.Current.CancellationToken);

        p.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StartAsync_ProviderThrows_AbortsAndPropagates()
    {
        var first = new RecordingSeedProvider("first", priority: 0);
        var failing = new ThrowingSeedProvider("failing", priority: 1);
        var third = new RecordingSeedProvider("third", priority: 2);

        var sut = CreateSut([first, failing, third], new TenancyOptions { BootstrapDefaultTenant = true });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.StartAsync(TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("seed boom");

        first.Calls.Count.ShouldBe(1);
        // Provider after the throwing one must not run — bootstrap aborts.
        third.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_OpensTenantScopeBypass_ForAuditTrail()
    {
        var bypass = Substitute.For<ITenantScopeBypass>();
        var disposable = Substitute.For<IDisposable>();
        bypass.BeginBypass(Arg.Any<string>()).Returns(disposable);

        var sut = new DefaultTenantBootstrapService(
            [],
            Options.Create(new TenancyOptions { BootstrapDefaultTenant = true }),
            bypass,
            NullLogger<DefaultTenantBootstrapService>.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        bypass.Received(1).BeginBypass(Arg.Is<string>(s => s.Contains("bootstrap")));
        disposable.Received(1).Dispose();
    }

    [Fact]
    public async Task StartAsync_FlagDisabled_DoesNotOpenBypass()
    {
        var bypass = Substitute.For<ITenantScopeBypass>();
        var sut = new DefaultTenantBootstrapService(
            [],
            Options.Create(new TenancyOptions { BootstrapDefaultTenant = false }),
            bypass,
            NullLogger<DefaultTenantBootstrapService>.Instance);

        await sut.StartAsync(TestContext.Current.CancellationToken);

        bypass.DidNotReceive().BeginBypass(Arg.Any<string>());
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        var sut = CreateSut([], new TenancyOptions { BootstrapDefaultTenant = true });
        await Should.NotThrowAsync(() => sut.StopAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_Cancellation_ShortCircuitsBeforeNextProvider()
    {
        using var cts = new CancellationTokenSource();
        var p1 = new RecordingSeedProvider("p1", priority: 0, onApply: _ => cts.Cancel());
        var p2 = new RecordingSeedProvider("p2", priority: 1);

        var sut = CreateSut([p1, p2], new TenancyOptions { BootstrapDefaultTenant = true });

        await Should.ThrowAsync<OperationCanceledException>(() => sut.StartAsync(cts.Token));

        p1.Calls.Count.ShouldBe(1);
        p2.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void DefaultTenantId_MatchesConfiguredTenantContextDefault()
    {
        DefaultTenantBootstrapService.DefaultTenantId.ShouldBe(ConfiguredTenantContext.DefaultTenantId);
    }

    private static DefaultTenantBootstrapService CreateSut(
        IEnumerable<ITenantSeedProvider> providers,
        TenancyOptions options)
    {
        return new DefaultTenantBootstrapService(
            providers,
            Options.Create(options),
            new TenantScopeBypass(NullLogger<TenantScopeBypass>.Instance),
            NullLogger<DefaultTenantBootstrapService>.Instance);
    }

    private sealed class RecordingSeedProvider : ITenantSeedProvider
    {
        private readonly Action<string>? _onApply;
        private readonly ConcurrentQueue<string> _calls = new();

        public RecordingSeedProvider(string id, int priority, Action<string>? onApply = null)
        {
            Id = id;
            Priority = priority;
            _onApply = onApply;
        }

        public string Id { get; }
        public int Priority { get; }
        public IReadOnlyList<string> Calls => _calls.ToArray();

        public Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
        {
            _calls.Enqueue(tenantId);
            _onApply?.Invoke(tenantId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSeedProvider(string id, int priority) : ITenantSeedProvider
    {
        public string Id { get; } = id;
        public int Priority { get; } = priority;

        public Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("seed boom");
    }
}