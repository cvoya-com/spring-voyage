// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Wiring-level integration tests for the #491 resolver pipeline. Exercises
/// the real <see cref="DefaultOrchestrationStrategyResolver"/> against a
/// real <see cref="IServiceScopeFactory"/> with every strategy key
/// registered. The <see cref="IOrchestrationStrategyProvider"/> is
/// substituted so these tests stay focused on the resolver's precedence
/// ladder and scope handling; the DB extraction is covered by the pure
/// <c>ExtractStrategyKey</c> unit tests under
/// <see cref="DbOrchestrationStrategyProviderTests"/>.
/// </summary>
public class ManifestOrchestrationStrategyTests
{
    [Fact]
    public async Task ResolveAsync_ManifestDeclaresLabelRouted_PicksKeyedRegistration()
    {
        await using var fixture = TestFixture.Create(providerKey: "label-routed");

        await using var lease = await fixture.Resolver.ResolveAsync(
            "triage", TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("label-routed");
        lease.Strategy.ShouldBe(fixture.LabelRoutedStrategy);
    }

    [Fact]
    public async Task ResolveAsync_NoManifestBlock_FallsBackToUnkeyedDefault()
    {
        await using var fixture = TestFixture.Create(providerKey: null);

        await using var lease = await fixture.Resolver.ResolveAsync(
            TestSlugIds.HexFor("plain-team"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBeNull();
        lease.Strategy.ShouldBe(fixture.DefaultStrategy);
    }

    [Fact]
    public async Task ResolveAsync_NoManifestButPolicyHasLabelRouting_InfersLabelRouted()
    {
        // ADR-0007 revisit criterion: when the label-routing slot is set on
        // UnitPolicy but no manifest strategy is declared, the resolver
        // should infer label-routed.
        await using var fixture = TestFixture.Create(
            providerKey: null,
            policy: new UnitPolicy(LabelRouting: new LabelRoutingPolicy()));

        await using var lease = await fixture.Resolver.ResolveAsync(
            TestSlugIds.HexFor("inferred"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("label-routed");
        lease.Strategy.ShouldBe(fixture.LabelRoutedStrategy);
    }

    [Fact]
    public async Task ResolveAsync_ManifestWorkflow_PicksWorkflowKey()
    {
        await using var fixture = TestFixture.Create(providerKey: "workflow");

        await using var lease = await fixture.Resolver.ResolveAsync(
            TestSlugIds.HexFor("pipeline"), TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBe("workflow");
        lease.Strategy.ShouldBe(fixture.WorkflowStrategy);
    }

    [Fact]
    public async Task ResolveAsync_UnknownManifestKey_FallsBackToDefault()
    {
        // Degraded-but-alive: an operator declared a key that the host does
        // not register. The resolver should log a warning and keep
        // dispatching through the default rather than hard-failing every
        // message.
        await using var fixture = TestFixture.Create(providerKey: "non-existent");

        await using var lease = await fixture.Resolver.ResolveAsync(
            "broken-unit", TestContext.Current.CancellationToken);

        lease.ResolvedKey.ShouldBeNull();
        lease.Strategy.ShouldBe(fixture.DefaultStrategy);
    }

    private sealed class TestFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _sp;

        private TestFixture(
            ServiceProvider sp,
            IOrchestrationStrategyResolver resolver,
            IOrchestrationStrategy aiStrategy,
            IOrchestrationStrategy labelRoutedStrategy,
            IOrchestrationStrategy workflowStrategy,
            IOrchestrationStrategy defaultStrategy)
        {
            _sp = sp;
            Resolver = resolver;
            AiStrategy = aiStrategy;
            LabelRoutedStrategy = labelRoutedStrategy;
            WorkflowStrategy = workflowStrategy;
            DefaultStrategy = defaultStrategy;
        }

        public IOrchestrationStrategyResolver Resolver { get; }
        public IOrchestrationStrategy AiStrategy { get; }
        public IOrchestrationStrategy LabelRoutedStrategy { get; }
        public IOrchestrationStrategy WorkflowStrategy { get; }
        public IOrchestrationStrategy DefaultStrategy { get; }

        public static TestFixture Create(string? providerKey, UnitPolicy? policy = null)
        {
            var services = new ServiceCollection();

            var ai = Substitute.For<IOrchestrationStrategy>();
            var labelRouted = Substitute.For<IOrchestrationStrategy>();
            var workflow = Substitute.For<IOrchestrationStrategy>();
            var unkeyedDefault = Substitute.For<IOrchestrationStrategy>();

            services.AddKeyedSingleton<IOrchestrationStrategy>("ai", ai);
            services.AddKeyedSingleton<IOrchestrationStrategy>("label-routed", labelRouted);
            services.AddKeyedSingleton<IOrchestrationStrategy>("workflow", workflow);
            services.AddSingleton<IOrchestrationStrategy>(unkeyedDefault);

            if (policy is not null)
            {
                var policyRepo = Substitute.For<IUnitPolicyRepository>();
                policyRepo.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(policy);
                services.AddScoped(_ => policyRepo);
            }

            var provider = Substitute.For<IOrchestrationStrategyProvider>();
            provider.GetStrategyKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(providerKey);
            services.AddSingleton(provider);

            services.AddSingleton<IOrchestrationStrategyResolver, DefaultOrchestrationStrategyResolver>();
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

            var sp = services.BuildServiceProvider();
            return new TestFixture(
                sp,
                sp.GetRequiredService<IOrchestrationStrategyResolver>(),
                ai,
                labelRouted,
                workflow,
                unkeyedDefault);
        }

        public ValueTask DisposeAsync()
        {
            _sp.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}