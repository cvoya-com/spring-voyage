// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the <see cref="ISkillRegistry"/> adapter over the expertise-
/// directory-driven catalog (#359). Focused on the DI extension seam —
/// skill callers get the live expertise surface through the existing
/// registry plumbing, and downstream hosts can swap the invoker for a
/// test double without touching callers.
/// </summary>
public class ExpertiseSkillRegistryTests
{
    private readonly IExpertiseSkillCatalog _catalog = Substitute.For<IExpertiseSkillCatalog>();

    private static ExpertiseSkill MakeSkill(string name, Address target)
    {
        var domain = new ExpertiseDomain(name, $"{name} desc", ExpertiseLevel.Advanced, "{\"type\":\"object\"}");
        var tool = new ToolDefinition(
            ExpertiseSkillNaming.GetSkillName(domain),
            domain.Description,
            ExpertiseSkillNaming.ParseSchemaOrEmpty(domain.InputSchemaJson),
            ToolCategories.Expertise);
        return new ExpertiseSkill(tool.Name, tool, target, new ExpertiseEntry(domain, target, new[] { target }));
    }

    [Fact]
    public async Task InvokeAsync_DispatchesThroughISkillInvoker_Swappable()
    {
        // A test-double invoker proves the ISkillInvoker seam is swappable —
        // the registry depends only on the interface, so a downstream host
        // (e.g. the #539 A2A gateway) can register its own implementation
        // and callers do not change.
        var catalog = Substitute.For<IExpertiseSkillCatalog>();
        var swappedInvoker = new StubSkillInvoker();

        var registry = new ExpertiseSkillRegistry(catalog, swappedInvoker, NullLoggerFactory.Instance);

        using var args = JsonDocument.Parse("{\"x\":1}");
        var result = await registry.InvokeAsync("sv.expertise.python", args.RootElement.Clone(), TestContext.Current.CancellationToken);

        swappedInvoker.ObservedCalls.ShouldHaveSingleItem();
        swappedInvoker.ObservedCalls[0].SkillName.ShouldBe("sv.expertise.python");
        result.GetProperty("stub").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_InvokerReportsSkillNotFound_ThrowsSkillNotFoundException()
    {
        var catalog = Substitute.For<IExpertiseSkillCatalog>();
        var invoker = Substitute.For<ISkillInvoker>();
        invoker.InvokeAsync(Arg.Any<SkillInvocation>(), Arg.Any<CancellationToken>())
            .Returns(SkillInvocationResult.Failure("SKILL_NOT_FOUND", "unknown"));

        var registry = new ExpertiseSkillRegistry(catalog, invoker, NullLoggerFactory.Instance);

        using var args = JsonDocument.Parse("{}");
        await Should.ThrowAsync<SkillNotFoundException>(
            () => registry.InvokeAsync("sv.expertise.unknown", args.RootElement.Clone(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ServiceRegistration_RegistersExpertiseRegistryAsOneOfTheISkillRegistries()
    {
        // Lightweight reflection-free check that TryAdd semantics let a
        // downstream host pre-register a replacement ISkillInvoker and keep
        // it. We simulate the DI bootstrap sequence without calling the full
        // AddCvoyaSpringDapr (which requires EF / Dapr configuration): we
        // register the types the rework owns in the same order.
        var services = new ServiceCollection();

        var preregistered = Substitute.For<ISkillInvoker>();
        services.AddSingleton(preregistered);

        services.TryAddSingleton<IExpertiseSkillCatalog>(Substitute.For<IExpertiseSkillCatalog>());
        services.TryAddSingleton<ISkillInvoker, MessageRouterSkillInvoker>();
        services.TryAddSingleton<ExpertiseSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, ExpertiseSkillRegistry>(
            sp => sp.GetRequiredService<ExpertiseSkillRegistry>()));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        using var provider = services.BuildServiceProvider();

        // Pre-registered invoker wins (TryAdd skipped the default).
        provider.GetRequiredService<ISkillInvoker>().ShouldBeSameAs(preregistered);

        var registries = provider.GetServices<ISkillRegistry>().ToList();
        registries.ShouldContain(r => r is ExpertiseSkillRegistry);
    }

    /// <summary>
    /// Trivial test double — verifies <see cref="ISkillInvoker"/> is the
    /// only seam the registry depends on and that alternative implementations
    /// can be plugged in without touching call sites.
    /// </summary>
    private sealed class StubSkillInvoker : ISkillInvoker
    {
        public List<SkillInvocation> ObservedCalls { get; } = new();

        public Task<SkillInvocationResult> InvokeAsync(
            SkillInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            ObservedCalls.Add(invocation);
            using var doc = JsonDocument.Parse("{\"stub\":true}");
            return Task.FromResult(SkillInvocationResult.Success(doc.RootElement.Clone()));
        }
    }
}
