// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Slug;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackThreadSlugBuilder"/> — the Slack-thread
/// parent-message slug builder defined by
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061 §4</see>.
/// The four worked examples in §4 are pinned verbatim; the additional
/// tests cover the per-thread Hat pinning (ADR-0062 §5),
/// <c>PrimaryHumanId</c> fallback, set-uniqueness for distinct threads
/// sharing humans, and unicode/punctuation slugification.
/// </summary>
public class SlackThreadSlugBuilderTests
{
    private static readonly Guid BoundTenantUserId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");

    // The four worked examples in ADR-0061 §4 use the labels alex, bob,
    // research, morgan. To make the slug-builder's deterministic sort
    // (by Address.Id) emit the labels in the verbatim order the ADR
    // requires, the test addresses are assigned Guids whose ordering
    // matches the desired slug order.
    //
    // Encoding: the leading hex digit of the Guid determines the sort
    // position. alex is the Hat (always dropped), so its id is
    // arbitrary; bob/research/morgan are ordered as 1, 2, 3 so the
    // emitted slug is "bob-research-morgan" / "bob-morgan" etc.
    private static readonly Guid AlexId = new("00000000-0000-0000-0000-00000000aaaa");
    private static readonly Guid BobId = new("00000001-0000-0000-0000-000000000000");
    private static readonly Guid ResearchId = new("00000002-0000-0000-0000-000000000000");
    private static readonly Guid MorganId = new("00000003-0000-0000-0000-000000000000");

    private static readonly Address AlexAddress = new(Address.HumanScheme, AlexId);
    private static readonly Address BobAddress = new(Address.AgentScheme, BobId);
    private static readonly Address ResearchAddress = new(Address.UnitScheme, ResearchId);
    private static readonly Address MorganAddress = new(Address.HumanScheme, MorganId);

    // ---- ADR-0061 §4 worked examples ----

    [Fact]
    public async Task BuildSlugAsync_TwoParticipants_DropsHat_EmitsAgentName()
    {
        // {human:alex, agent:bob} → "sv-bob"
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob");
    }

    [Fact]
    public async Task BuildSlugAsync_ThreeParticipants_DropsHat_EmitsAgentAndUnit()
    {
        // {human:alex, agent:bob, unit:research} → "sv-bob-research"
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(ResearchAddress, "Research");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress, ResearchAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob-research");
    }

    [Fact]
    public async Task BuildSlugAsync_FourParticipantsWithSecondHuman_KeepsBothHumans()
    {
        // {human:alex, agent:bob, unit:research, human:morgan}
        //  → "sv-bob-research-morgan"
        // Only the operator's own Hat (alex) drops; morgan is a second
        // SV human and stays in the slug (ADR-0061 §4 set-uniqueness).
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(ResearchAddress, "Research")
            .WithDisplayName(MorganAddress, "Morgan");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress, ResearchAddress, MorganAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob-research-morgan");
    }

    [Fact]
    public async Task BuildSlugAsync_TwoHumansAndAgent_DropsOnlyPrimaryHuman()
    {
        // {human:alex, human:morgan, agent:bob} → "sv-bob-morgan"
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(MorganAddress, "Morgan");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, MorganAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob-morgan");
    }

    // ---- Set uniqueness ----

    [Fact]
    public async Task BuildSlugAsync_DistinctParticipantSetsSharingHumans_ProduceDistinctSlugs()
    {
        // ADR-0061 §4: collapsing every SV human mapped to the same
        // TenantUser to one name would lose set uniqueness — {alex,bob}
        // and {alex,morgan,bob} would render identically. The slug
        // preserves every SV human except exactly the one Hat to drop.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(MorganAddress, "Morgan");

        var slugA = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        var slugB = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, MorganAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slugA.ShouldBe("sv-bob");
        slugB.ShouldBe("sv-bob-morgan");
        slugA.ShouldNotBe(slugB);
    }

    // ---- Hat resolution ----

    [Fact]
    public async Task BuildSlugAsync_NewThread_FallsBackToPrimaryHumanId()
    {
        // ADR-0062 §3 hierarchy: with no per-thread reply pin (new
        // thread, threadId = Guid.Empty), the resolver returns the
        // TenantUser's PrimaryHumanId — that's the Hat the slug drops.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(MorganAddress, "Morgan");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, MorganAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        // Primary (alex) dropped, morgan stays.
        slug.ShouldBe("sv-bob-morgan");

        // The resolver was called with threadId=null (new-thread path).
        await harness.HatResolver
            .Received(1)
            .PickFromAsync(
                BoundTenantUserId,
                null,
                null,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildSlugAsync_ReplyThread_UsesThreadPinnedHat()
    {
        // ADR-0062 §5 per-thread Hat pinning: the resolver returns the
        // non-primary Hat the thread came in on. The slug drops that
        // Hat (morgan), not the primary (alex), so the bound user's
        // primary appears in the slug like any other SV human.
        var ct = TestContext.Current.CancellationToken;
        var threadId = new Guid("12345678-0000-0000-0000-000000000000");
        var harness = TestHarness.Create()
            .WithThreadHat(threadId, MorganAddress)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(AlexAddress, "Alex");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, MorganAddress, BobAddress },
            BoundTenantUserId,
            threadId,
            ct);

        // Morgan is the Hat the thread came in on; alex stays.
        // Sort by Guid: alex(0...aaaa) < bob(00000001) < morgan(00000003)
        // dropping morgan → alex, bob → "sv-alex-bob".
        // (alex's id has the high-end nibble "aaaa" which sorts after
        // bob's "00000001". Guid.CompareTo orders by the _a field
        // first; alex's _a = 0, bob's _a = 1, so alex < bob.)
        slug.ShouldBe("sv-alex-bob");

        await harness.HatResolver
            .Received(1)
            .PickFromAsync(
                BoundTenantUserId,
                null,
                threadId,
                Arg.Any<CancellationToken>());
    }

    // ---- Slugification ----

    [Fact]
    public async Task BuildSlugAsync_DisplayNameWithSpaces_CollapsesToHyphens()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Project Apollo");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-project-apollo");
    }

    [Fact]
    public async Task BuildSlugAsync_DisplayNameWithUnicode_StripsToAsciiSafeRun()
    {
        // ADR-0061 §4 doesn't pin a unicode rule; we follow the
        // existing convention from ExpertiseSkill.Slugify: replace
        // non-[a-z0-9] with the separator and collapse runs.
        // "Café Müller" → "caf-m-ller".
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Café Müller");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-caf-m-ller");
    }

    [Fact]
    public async Task BuildSlugAsync_DisplayNameWithPunctuationRuns_CollapsesRuns()
    {
        // "R&D --- Team!!!" → "r-d-team".
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "R&D --- Team!!!");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-r-d-team");
    }

    // ---- Edge / contract ----

    [Fact]
    public async Task BuildSlugAsync_EmptyParticipants_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await harness.Builder.BuildSlugAsync(
                Array.Empty<Address>(),
                BoundTenantUserId,
                threadId: Guid.Empty,
                ct));

        ex.ParamName.ShouldBe("participants");
    }

    [Fact]
    public async Task BuildSlugAsync_NullParticipants_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await harness.Builder.BuildSlugAsync(
                null!,
                BoundTenantUserId,
                threadId: Guid.Empty,
                ct));
    }

    [Fact]
    public async Task BuildSlugAsync_EmptyBoundTenantUserId_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await harness.Builder.BuildSlugAsync(
                new[] { AlexAddress, BobAddress },
                Guid.Empty,
                threadId: Guid.Empty,
                ct));

        ex.ParamName.ShouldBe("boundTenantUserId");
    }

    [Fact]
    public async Task BuildSlugAsync_DuplicateParticipants_DedupsToSlugOnce()
    {
        // The participant set is a set, not a list. Duplicates in the
        // input vector collapse to one slug token (same canonicalisation
        // EfThreadRegistry uses).
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(AlexAddress)
            .WithDisplayName(BobAddress, "Bob");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { AlexAddress, BobAddress, BobAddress, AlexAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob");
    }

    [Fact]
    public async Task BuildSlugAsync_HatNotInParticipants_LeavesEveryoneInSlug()
    {
        // Defensive: if the resolver returns a Hat that is not in the
        // participant set (unusual but possible if the slug is built
        // for a thread the bound user doesn't directly participate in),
        // no participant is dropped — every name appears in the slug.
        var unusedHat = new Address(Address.HumanScheme,
            new Guid("00000000-ffff-0000-0000-000000000000"));

        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create()
            .WithPrimaryHat(unusedHat)
            .WithDisplayName(BobAddress, "Bob")
            .WithDisplayName(MorganAddress, "Morgan");

        var slug = await harness.Builder.BuildSlugAsync(
            new[] { BobAddress, MorganAddress },
            BoundTenantUserId,
            threadId: Guid.Empty,
            ct);

        slug.ShouldBe("sv-bob-morgan");
    }

    // ---- Slugify helper (direct) ----

    [Theory]
    [InlineData("Bob", "bob")]
    [InlineData("Project Apollo", "project-apollo")]
    [InlineData("R&D Team", "r-d-team")]
    [InlineData("  leading  trailing  ", "leading-trailing")]
    [InlineData("---weird---", "weird")]
    [InlineData("Café", "caf")]
    [InlineData("123abc", "123abc")]
    [InlineData("", "")]
    public void Slugify_ProducesAsciiSafeLowercaseRuns(string input, string expected)
    {
        SlackThreadSlugBuilder.Slugify(input).ShouldBe(expected);
    }

    // ---- Harness ----

    private sealed class TestHarness
    {
        public SlackThreadSlugBuilder Builder { get; }
        public ITenantUserHumanResolver HatResolver { get; }
        public IParticipantDisplayNameResolver NameResolver { get; }

        private TestHarness(
            SlackThreadSlugBuilder builder,
            ITenantUserHumanResolver hatResolver,
            IParticipantDisplayNameResolver nameResolver)
        {
            Builder = builder;
            HatResolver = hatResolver;
            NameResolver = nameResolver;
        }

        public static TestHarness Create()
        {
            var services = new ServiceCollection();

            var hatResolver = Substitute.For<ITenantUserHumanResolver>();
            var nameResolver = Substitute.For<IParticipantDisplayNameResolver>();

            // Default: every address resolves to its Path (the no-dash
            // hex of its Guid) — tests that care about specific labels
            // override this via WithDisplayName.
            nameResolver
                .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var addr = call.ArgAt<string>(0);
                    return new ValueTask<string>(addr);
                });

            services.AddSingleton(hatResolver);
            services.AddSingleton(nameResolver);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var builder = new SlackThreadSlugBuilder(scopeFactory);

            return new TestHarness(builder, hatResolver, nameResolver);
        }

        public TestHarness WithPrimaryHat(Address hatAddress)
        {
            // The resolver is called with threadId=null for new-thread
            // paths and returns the primary Hat per ADR-0062 §3.
            HatResolver
                .PickFromAsync(
                    Arg.Any<Guid>(),
                    null,
                    null,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(hatAddress));
            return this;
        }

        public TestHarness WithThreadHat(Guid threadId, Address hatAddress)
        {
            // The resolver returns a thread-pinned Hat per ADR-0062 §5.
            HatResolver
                .PickFromAsync(
                    Arg.Any<Guid>(),
                    null,
                    threadId,
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(hatAddress));
            return this;
        }

        public TestHarness WithDisplayName(Address address, string displayName)
        {
            NameResolver
                .ResolveAsync(address.ToString(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<string>(displayName));
            return this;
        }
    }
}
