// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="InboundEnvelopeResolver"/> — specifically the
/// recipient-set derivation that feeds the envelope's <c>to</c> field.
/// <para>
/// #2890: the rendering itself (<c>InboundEnvelopeBuilder.Render</c>) was
/// already covered, but the resolution that produces the recipient list —
/// "take the thread's persisted participant set, subtract the sender" — had
/// <b>zero</b> tests. That derivation is the live #2887 N-party seam: a
/// regression that stopped subtracting the sender, or collapsed to the
/// single per-hop recipient, would ship green. These tests drive the real
/// <c>ResolveRecipientsAsync</c> through the public
/// <see cref="InboundEnvelopeResolver.RenderEnvelopeAsync"/> and assert the
/// rendered <c>to</c> line — i.e. the resolver's transformation of a
/// controlled participant set, not a value any stub returned.
/// </para>
/// </summary>
public class InboundEnvelopeResolverTests
{
    private static readonly Address Sender =
        new(Address.HumanScheme, new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly Address AgentA =
        new(Address.AgentScheme, new Guid("22222222-2222-2222-2222-222222222222"));
    private static readonly Address AgentB =
        new(Address.AgentScheme, new Guid("33333333-3333-3333-3333-333333333333"));
    private static readonly Address UnitC =
        new(Address.UnitScheme, new Guid("44444444-4444-4444-4444-444444444444"));

    private const string ThreadId = "0123456789abcdef0123456789abcdef";

    private sealed class Harness
    {
        public InboundEnvelopeResolver Resolver { get; }
        public IThreadRegistry Registry { get; }
        public IDirectoryService Directory { get; }

        private Harness(InboundEnvelopeResolver resolver, IThreadRegistry registry, IDirectoryService directory)
        {
            Resolver = resolver;
            Registry = registry;
            Directory = directory;
        }

        public static Harness Create()
        {
            var registry = Substitute.For<IThreadRegistry>();
            var directory = Substitute.For<IDirectoryService>();
            // Sender has no directory row (the connector/unknown-sender case);
            // the envelope must still render. Returning null also keeps the
            // `to`-line assertions free of an injected display name.
            directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns((DirectoryEntry?)null);

            var services = new ServiceCollection();
            services.AddSingleton(registry);
            services.AddSingleton(directory);
            var provider = services.BuildServiceProvider();

            return new Harness(
                new InboundEnvelopeResolver(provider.GetRequiredService<IServiceScopeFactory>()),
                registry,
                directory);
        }
    }

    private static Message Inbound(Address to, string? threadId) => new(
        Guid.NewGuid(),
        Sender,
        to,
        MessageType.Domain,
        threadId,
        JsonSerializer.SerializeToElement("hello"),
        new DateTimeOffset(2026, 5, 24, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RenderEnvelope_NPartyThread_ListsEveryRecipientExceptTheSender()
    {
        // The thread carries the full canonical set {sender, A, B, C}; the
        // envelope's `to` must name every party except the sender — so any
        // one recipient sees the others (#2887). Inbound.To is a single hop
        // recipient (AgentA); the resolved `to` must widen past it to B and C.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();
        harness.Registry.ResolveAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(new ThreadRegistryEntry(
                ThreadId,
                new[] { Sender, AgentA, AgentB, UnitC },
                DateTimeOffset.UtcNow));

        var rendered = await harness.Resolver.RenderEnvelopeAsync(Inbound(AgentA, ThreadId), ct);

        // Exact `to` line: all three non-sender participants, in the
        // registry's canonical order, and the sender absent. Inverting the
        // subtraction (sender present) or the fallback (only AgentA) both
        // break this.
        rendered.ShouldContain($"- to: [{AgentA}, {AgentB}, {UnitC}]");
        rendered.ShouldContain($"- from: {Sender}");
    }

    [Fact]
    public async Task RenderEnvelope_NoThreadId_FallsBackToTheSingleHopRecipient()
    {
        // No thread to resolve a roster from — the envelope falls back to the
        // per-hop recipient so it is still useful.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();

        var rendered = await harness.Resolver.RenderEnvelopeAsync(Inbound(AgentA, threadId: null), ct);

        rendered.ShouldContain($"- to: [{AgentA}]");
        // The registry is never consulted when there is no thread id.
        await harness.Registry.DidNotReceive()
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenderEnvelope_ThreadResolvesToSenderOnly_FallsBackToTheHopRecipient()
    {
        // After subtracting the sender the roster is empty; rather than render
        // an empty `to`, the resolver falls back to the per-hop recipient.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();
        harness.Registry.ResolveAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(new ThreadRegistryEntry(
                ThreadId,
                new[] { Sender },
                DateTimeOffset.UtcNow));

        var rendered = await harness.Resolver.RenderEnvelopeAsync(Inbound(AgentB, ThreadId), ct);

        rendered.ShouldContain($"- to: [{AgentB}]");
    }

    [Fact]
    public async Task RenderEnvelope_UnresolvableThread_FallsBackToTheHopRecipient()
    {
        // A thread id that resolves to nothing (race during create, stale id)
        // must not throw — fall back to the per-hop recipient.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();
        harness.Registry.ResolveAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns((ThreadRegistryEntry?)null);

        var rendered = await harness.Resolver.RenderEnvelopeAsync(Inbound(UnitC, ThreadId), ct);

        rendered.ShouldContain($"- to: [{UnitC}]");
    }

    // ── #3056 batched delivery ───────────────────────────────────────────

    [Fact]
    public async Task RenderEnvelope_SingleMessageBatch_RendersIdenticallyToSingleOverload()
    {
        // A one-element batch must be byte-for-byte identical to the single
        // overload — the common one-message-per-turn case is unchanged.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();
        harness.Registry.ResolveAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(new ThreadRegistryEntry(ThreadId, new[] { Sender, AgentA }, DateTimeOffset.UtcNow));
        var message = Inbound(AgentA, ThreadId);

        var single = await harness.Resolver.RenderEnvelopeAsync(message, ct);
        var batchOfOne = await harness.Resolver.RenderEnvelopeAsync(new[] { message }, ct);

        batchOfOne.ShouldBe(single);
        batchOfOne.ShouldStartWith("You received a message.");
    }

    [Fact]
    public async Task RenderEnvelope_MultipleMessages_RendersOrderedSelfDescribedBatch()
    {
        // A multi-message batch names every message self-described, in order,
        // under its own header, and frames the set so the runtime reasons over
        // the whole thing before acting.
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();
        harness.Registry.ResolveAsync(ThreadId, Arg.Any<CancellationToken>())
            .Returns(new ThreadRegistryEntry(ThreadId, new[] { Sender, AgentA }, DateTimeOffset.UtcNow));

        var first = Inbound(AgentA, ThreadId);
        var second = Inbound(AgentA, ThreadId);
        var third = Inbound(AgentA, ThreadId);

        var rendered = await harness.Resolver.RenderEnvelopeAsync(new[] { first, second, third }, ct);

        // Batch framing: the count and the reason-over-the-set guidance.
        rendered.ShouldContain("You received 3 messages in this conversation");
        rendered.ShouldContain("read the whole set", Case.Insensitive);
        rendered.ShouldContain("supersede");

        // Each message is self-described under its own ordered header.
        rendered.ShouldContain("--- message 1 of 3 ---");
        rendered.ShouldContain("--- message 2 of 3 ---");
        rendered.ShouldContain("--- message 3 of 3 ---");
        foreach (var m in new[] { first, second, third })
        {
            rendered.ShouldContain(GuidFormatter.Format(m.Id),
                customMessage: "every batched message must carry its own message_id.");
        }

        // Ordering: message 1's id precedes message 2's, which precedes 3's.
        var i1 = rendered.IndexOf(GuidFormatter.Format(first.Id), StringComparison.Ordinal);
        var i2 = rendered.IndexOf(GuidFormatter.Format(second.Id), StringComparison.Ordinal);
        var i3 = rendered.IndexOf(GuidFormatter.Format(third.Id), StringComparison.Ordinal);
        i1.ShouldBeLessThan(i2);
        i2.ShouldBeLessThan(i3);
    }

    [Fact]
    public async Task RenderEnvelope_EmptyBatch_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = Harness.Create();

        await Should.ThrowAsync<ArgumentException>(
            async () => await harness.Resolver.RenderEnvelopeAsync(Array.Empty<Message>(), ct));
    }
}
