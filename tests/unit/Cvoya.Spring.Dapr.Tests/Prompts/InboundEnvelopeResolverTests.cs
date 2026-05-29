// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
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
}
