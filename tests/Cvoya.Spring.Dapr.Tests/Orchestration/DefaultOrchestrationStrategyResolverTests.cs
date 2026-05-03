// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultOrchestrationStrategyResolver"/> (#491).
/// Covers the three-step precedence ladder:
/// <list type="number">
///   <item>Manifest-declared key wins when registered;</item>
///   <item><c>LabelRouting</c> policy implies <c>label-routed</c> when no
///         manifest key is declared;</item>
///   <item>Falls back to the unkeyed default otherwise.</item>
/// </list>
/// </summary>
public class DefaultOrchestrationStrategyResolverTests
{
    [Fact]
    public async Task ResolveAsync_NoManifestAndNoPolicy_ReturnsUnkeyedDefault()
    {
        var fake = BuildFixture();
        fake.StrategyProvider
            .GetStrategyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        // No IUnitPolicyRepository registered — the fallback kicks in.

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("some-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBeNull();
        lease.Strategy.ShouldBe(fake.DefaultStrategy);
    }

    [Fact]
    public async Task ResolveAsync_ManifestDeclaredKey_ResolvesKeyedRegistration()
    {
        var fake = BuildFixture();
        fake.StrategyProvider
            .GetStrategyKeyAsync(TestSlugIds.HexFor("triage-unit"), Arg.Any<CancellationToken>())
            .Returns("label-routed");

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("triage-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("label-routed");
        lease.Strategy.ShouldBe(fake.LabelRoutedStrategy);
    }

    [Fact]
    public async Task ResolveAsync_UnknownManifestKey_FallsBackToDefault()
    {
        var fake = BuildFixture();
        fake.StrategyProvider
            .GetStrategyKeyAsync(TestSlugIds.HexFor("weird-unit"), Arg.Any<CancellationToken>())
            .Returns("non-existent-strategy");

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("weird-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBeNull();
        lease.Strategy.ShouldBe(fake.DefaultStrategy);
    }

    [Fact]
    public async Task ResolveAsync_NoManifestButLabelRoutingPolicy_InfersLabelRouted()
    {
        var fake = BuildFixture(wireLabelRoutingPolicy: true);
        fake.StrategyProvider
            .GetStrategyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("inferred-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("label-routed");
        lease.Strategy.ShouldBe(fake.LabelRoutedStrategy);
    }

    [Fact]
    public async Task ResolveAsync_NoManifestAndEmptyPolicy_FallsBackToDefault()
    {
        var fake = BuildFixture(wireLabelRoutingPolicy: false, wireRepository: true);
        fake.StrategyProvider
            .GetStrategyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("empty-policy-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBeNull();
        lease.Strategy.ShouldBe(fake.DefaultStrategy);
    }

    [Fact]
    public async Task ResolveAsync_ManifestKeyWinsOverPolicy()
    {
        // When both manifest and policy could apply, manifest wins. Here the
        // manifest declares 'ai' but policy says LabelRouting — the manifest
        // directive is the explicit operator intent and should win.
        var fake = BuildFixture(wireLabelRoutingPolicy: true);
        fake.StrategyProvider
            .GetStrategyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("ai");

        var resolver = new DefaultOrchestrationStrategyResolver(
            fake.StrategyProvider, fake.ScopeFactory, NullLoggerFactory.Instance);

        await using var lease = await resolver.ResolveAsync(
            TestSlugIds.HexFor("ai-unit"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("ai");
        lease.Strategy.ShouldBe(fake.AiStrategy);
    }

    private static Fixture BuildFixture(
        bool wireLabelRoutingPolicy = false,
        bool wireRepository = false)
    {
        var services = new ServiceCollection();

        var aiStrategy = Substitute.For<IOrchestrationStrategy>();
        var labelRoutedStrategy = Substitute.For<IOrchestrationStrategy>();
        var defaultStrategy = Substitute.For<IOrchestrationStrategy>();

        services.AddKeyedSingleton<IOrchestrationStrategy>("ai", aiStrategy);
        services.AddKeyedSingleton<IOrchestrationStrategy>("label-routed", labelRoutedStrategy);
        services.AddSingleton<IOrchestrationStrategy>(defaultStrategy);

        if (wireLabelRoutingPolicy || wireRepository)
        {
            var repo = Substitute.For<IUnitPolicyRepository>();
            var policy = wireLabelRoutingPolicy
                ? new UnitPolicy(LabelRouting: new LabelRoutingPolicy())
                : UnitPolicy.Empty;
            repo.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(policy);
            services.AddScoped(_ => repo);
        }

        var sp = services.BuildServiceProvider();
        return new Fixture(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IOrchestrationStrategyProvider>(),
            aiStrategy,
            labelRoutedStrategy,
            defaultStrategy);
    }

    private sealed record Fixture(
        IServiceScopeFactory ScopeFactory,
        IOrchestrationStrategyProvider StrategyProvider,
        IOrchestrationStrategy AiStrategy,
        IOrchestrationStrategy LabelRoutedStrategy,
        IOrchestrationStrategy DefaultStrategy);
}