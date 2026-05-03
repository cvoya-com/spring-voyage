// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for UnitActor's own-expertise surface (#412) plus the auto-seed
/// from <c>UnitDefinition</c> YAML on activation (#488).
/// </summary>
public class UnitActorExpertiseTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly UnitActor _actor;

    public UnitActorExpertiseTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestSlugIds.HexFor("test-unit"))
        });
        _actor = new UnitActor(
            host,
            loggerFactory,
            Substitute.For<IOrchestrationStrategy>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>());

        var field = typeof(Actor).GetField("<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_actor, _stateManager);

        _stateManager.TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.UnitOwnExpertise, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<ExpertiseDomain>>(false, default!));
    }

    [Fact]
    public async Task GetOwnExpertiseAsync_NoState_ReturnsEmpty()
    {
        var result = await _actor.GetOwnExpertiseAsync(TestContext.Current.CancellationToken);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_ReplacesState()
    {
        var domains = new[]
        {
            new ExpertiseDomain("python", "fastapi", ExpertiseLevel.Expert),
            new ExpertiseDomain("react", "next.js", ExpertiseLevel.Advanced),
        };
        List<ExpertiseDomain>? captured = null;
        _stateManager.SetStateAsync(
                StateKeys.UnitOwnExpertise,
                Arg.Do<List<ExpertiseDomain>>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_DedupesByName()
    {
        var domains = new[]
        {
            new ExpertiseDomain("python", "", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "", ExpertiseLevel.Expert),
        };
        List<ExpertiseDomain>? captured = null;
        _stateManager.SetStateAsync(
                StateKeys.UnitOwnExpertise,
                Arg.Do<List<ExpertiseDomain>>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        captured!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_IgnoresEntriesWithBlankName()
    {
        var domains = new[]
        {
            new ExpertiseDomain("", "empty", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "ok", ExpertiseLevel.Expert),
        };
        List<ExpertiseDomain>? captured = null;
        _stateManager.SetStateAsync(
                StateKeys.UnitOwnExpertise,
                Arg.Do<List<ExpertiseDomain>>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        captured!.Count.ShouldBe(1);
        captured[0].Name.ShouldBe("python");
    }

    /// <summary>
    /// When actor state has no own-expertise and the seed provider offers a
    /// YAML-declared list, activation writes it through the same
    /// <c>SetOwnExpertiseAsync</c> path. See #488.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_EmptyState_SeedsFromProvider()
    {
        var harness = BuildSeedHarness(
            stateHasValue: false,
            seed: new[]
            {
                new ExpertiseDomain("platform", "", ExpertiseLevel.Expert),
                new ExpertiseDomain("routing", "", ExpertiseLevel.Advanced),
            },
            out var stateManager);

        List<ExpertiseDomain>? captured = null;
        stateManager.SetStateAsync(
                StateKeys.UnitOwnExpertise,
                Arg.Do<List<ExpertiseDomain>>(v => captured = v),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await InvokeOnActivateAsync(harness);

        captured.ShouldNotBeNull();
        captured!.Count.ShouldBe(2);
        captured[0].Name.ShouldBe("platform");
        captured[1].Name.ShouldBe("routing");
    }

    /// <summary>
    /// Precedence rule: when actor state already holds a value (even an
    /// empty list written through <c>SetOwnExpertiseAsync</c>), the seed is
    /// NOT re-applied on activation. Keeps runtime operator edits
    /// authoritative across process restarts.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_StateHasValue_DoesNotSeed()
    {
        var harness = BuildSeedHarness(
            stateHasValue: true,
            seed: new[] { new ExpertiseDomain("should-not-seed", "", null) },
            out var stateManager);

        await InvokeOnActivateAsync(harness);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitOwnExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the seed provider returns null (no YAML declared) the activation
    /// path must not write anything to actor state.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_NoSeed_NoWrite()
    {
        var harness = BuildSeedHarness(
            stateHasValue: false,
            seed: null,
            out var stateManager);

        await InvokeOnActivateAsync(harness);

        await stateManager.DidNotReceive().SetStateAsync(
            StateKeys.UnitOwnExpertise,
            Arg.Any<List<ExpertiseDomain>>(),
            Arg.Any<CancellationToken>());
    }

    private static UnitActor BuildSeedHarness(
        bool stateHasValue,
        IReadOnlyList<ExpertiseDomain>? seed,
        out IActorStateManager stateManager)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        stateManager = Substitute.For<IActorStateManager>();
        stateManager
            .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.UnitOwnExpertise, Arg.Any<CancellationToken>())
            .Returns(stateHasValue
                ? new ConditionalValue<List<ExpertiseDomain>>(true, new List<ExpertiseDomain>())
                : new ConditionalValue<List<ExpertiseDomain>>(false, default!));

        var seedProvider = Substitute.For<IExpertiseSeedProvider>();
        seedProvider
            .GetUnitSeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(seed);

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions { ActorId = new ActorId(TestSlugIds.HexFor("seed-unit")) });
        var actor = new UnitActor(
            host,
            loggerFactory,
            Substitute.For<IOrchestrationStrategy>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>(),
            seedProvider);

        typeof(Actor).GetField("<StateManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(actor, stateManager);

        return actor;
    }

    private static Task InvokeOnActivateAsync(UnitActor actor)
    {
        var method = typeof(UnitActor).GetMethod(
            "OnActivateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.ShouldNotBeNull();
        return (Task)method!.Invoke(actor, Array.Empty<object>())!;
    }
}