// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for UnitActor's own-expertise surface (#412) plus the auto-seed
/// from <c>UnitDefinition</c> YAML on activation (#488). Per ADR-0040 /
/// #2049 own-expertise lives on the <c>unit_expertise</c> EF table; the
/// "actor state wins" precedence flag lives on
/// <c>unit_live_config.expertise_initialised</c>. The tests drive the
/// EF surface through <see cref="InMemoryUnitLiveConfigStore"/>.
/// </summary>
public class UnitActorExpertiseTests
{
    [Fact]
    public async Task GetOwnExpertiseAsync_NoState_ReturnsEmpty()
    {
        var (actor, _) = BuildActor();

        var result = await actor.GetOwnExpertiseAsync(TestContext.Current.CancellationToken);
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_ReplacesState()
    {
        var (actor, store) = BuildActor(out var unitGuid);

        var domains = new[]
        {
            new ExpertiseDomain("python", "fastapi", ExpertiseLevel.Expert),
            new ExpertiseDomain("react", "next.js", ExpertiseLevel.Advanced),
        };

        await actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.Length.ShouldBe(2);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_DedupesByName()
    {
        var (actor, store) = BuildActor(out var unitGuid);

        var domains = new[]
        {
            new ExpertiseDomain("python", "", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "", ExpertiseLevel.Expert),
        };

        await actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.Length.ShouldBe(1);
        // Last write wins on (Name, *), so the persisted level is Expert.
        fetched[0].Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_IgnoresEntriesWithBlankName()
    {
        var (actor, store) = BuildActor(out var unitGuid);

        var domains = new[]
        {
            new ExpertiseDomain("", "empty", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "ok", ExpertiseLevel.Expert),
        };

        await actor.SetOwnExpertiseAsync(domains, TestContext.Current.CancellationToken);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.Length.ShouldBe(1);
        fetched[0].Name.ShouldBe("python");
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_FlipsExpertiseInitialisedFlag_EvenForEmptyList()
    {
        // Even a deliberate clear must turn the flag on so the YAML
        // seed is not re-applied at next activation.
        var (actor, store) = BuildActor(out var unitGuid);

        (await store.HasOwnExpertiseSetAsync(unitGuid, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        await actor.SetOwnExpertiseAsync(Array.Empty<ExpertiseDomain>(), TestContext.Current.CancellationToken);

        (await store.HasOwnExpertiseSetAsync(unitGuid, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// When EF has no own-expertise record and the seed provider offers a
    /// YAML-declared list, activation writes it through the same
    /// <c>SetOwnExpertiseAsync</c> path. See #488.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_EmptyState_SeedsFromProvider()
    {
        var seed = new[]
        {
            new ExpertiseDomain("platform", "", ExpertiseLevel.Expert),
            new ExpertiseDomain("routing", "", ExpertiseLevel.Advanced),
        };

        var (actor, store) = BuildActor(out var unitGuid, alreadyInitialised: false, seed: seed);

        await InvokeOnActivateAsync(actor);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.Length.ShouldBe(2);
        fetched.ShouldContain(d => d.Name == "platform");
        fetched.ShouldContain(d => d.Name == "routing");
    }

    /// <summary>
    /// Precedence rule: when EF reports the unit as already initialised
    /// (any value, even an empty list, was written through
    /// <c>SetOwnExpertiseAsync</c>), the seed is NOT re-applied on
    /// activation. Keeps runtime operator edits authoritative across
    /// process restarts.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_StateHasValue_DoesNotSeed()
    {
        var (actor, store) = BuildActor(
            out var unitGuid,
            alreadyInitialised: true,
            seed: new[] { new ExpertiseDomain("should-not-seed", "", null) });

        await InvokeOnActivateAsync(actor);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.ShouldBeEmpty();
    }

    /// <summary>
    /// When the seed provider returns null (no YAML declared) the
    /// activation path must not write anything to EF.
    /// </summary>
    [Fact]
    public async Task OnActivateAsync_NoSeed_NoWrite()
    {
        var (actor, store) = BuildActor(out var unitGuid, alreadyInitialised: false, seed: null);

        await InvokeOnActivateAsync(actor);

        var fetched = await store.GetOwnExpertiseAsync(unitGuid, TestContext.Current.CancellationToken);
        fetched.ShouldBeEmpty();
        (await store.HasOwnExpertiseSetAsync(unitGuid, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    private static (UnitActor Actor, InMemoryUnitLiveConfigStore Store) BuildActor()
    {
        return BuildActor(out _, alreadyInitialised: false, seed: null);
    }

    private static (UnitActor Actor, InMemoryUnitLiveConfigStore Store) BuildActor(out Guid unitGuid)
    {
        return BuildActor(out unitGuid, alreadyInitialised: false, seed: null);
    }

    private static (UnitActor Actor, InMemoryUnitLiveConfigStore Store) BuildActor(
        out Guid unitGuid,
        bool alreadyInitialised,
        IReadOnlyList<ExpertiseDomain>? seed)
    {
        unitGuid = Guid.NewGuid();
        var actorId = unitGuid.ToString("N");

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var store = new InMemoryUnitLiveConfigStore();
        if (alreadyInitialised)
        {
            store.SetExpertiseInitialised(unitGuid);
        }

        var seedProvider = Substitute.For<IExpertiseSeedProvider>();
        seedProvider
            .GetUnitSeedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(seed);

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId),
        });

        var actor = new UnitActor(
            host,
            loggerFactory,
            Substitute.For<IRuntimeInvocationPath>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>(),
            new UnitStateCoordinator(store, Substitute.For<ILogger<UnitStateCoordinator>>()),
            new InMemoryUnitMemberGraphStore(),
            seedProvider);

        return (actor, store);
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
