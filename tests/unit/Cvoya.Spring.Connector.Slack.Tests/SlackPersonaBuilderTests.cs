// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackPersonaBuilder"/>. Pins the
/// display-name resolution + deterministic icon-URL shape per
/// ADR-0061 §3 ("Persona overrides for non-bound participants").
/// </summary>
public class SlackPersonaBuilderTests
{
    [Theory]
    [InlineData(Address.AgentScheme)]
    [InlineData(Address.UnitScheme)]
    [InlineData(Address.HumanScheme)]
    public async Task ResolveAsync_ForwardsCanonicalAddressToResolver_AndSurfacesReturnedName(
        string scheme)
    {
        // #2890: the prior three tests each stubbed the resolver to return a
        // name and asserted that same name back — mock-validates-mock, and the
        // scheme never reaches a branch (the builder forwards
        // `address.ToString()` identically for agent/unit/human). The real,
        // falsifiable behaviour is the *argument mapping*: the builder must
        // call the resolver with the participant's canonical address string.
        // The `Received` check fails if the builder ever forwarded the wrong
        // key (e.g. the raw Guid), which a return-value assertion alone would
        // not catch. A [Theory] across schemes documents that this holds for
        // every address kind without pretending each is a distinct path.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        var participant = new Address(scheme, new Guid("00000001-0000-0000-0000-000000000000"));
        harness.NameResolver.ResolveAsync(participant.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Resolved Name"));

        var persona = await harness.Builder.ResolveAsync(participant, ct);

        await harness.NameResolver.Received(1)
            .ResolveAsync(participant.ToString(), Arg.Any<CancellationToken>());
        persona.Username.ShouldBe("Resolved Name");
        persona.IconUrl.ShouldStartWith(SlackPersonaBuilder.PlaceholderIconBaseUrl);
        persona.IconUrl.ShouldContain("d=identicon");
    }

    [Fact]
    public async Task ResolveAsync_DifferentAddresses_ProduceDifferentIconSeeds()
    {
        // The icon URL is deterministic per address — two different
        // addresses must produce two different URLs so the visual
        // distinguishes them even when the display names happen to
        // match.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        var a = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        var b = new Address(Address.AgentScheme, new Guid("00000002-0000-0000-0000-000000000000"));
        harness.NameResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Same Name"));

        var pa = await harness.Builder.ResolveAsync(a, ct);
        var pb = await harness.Builder.ResolveAsync(b, ct);

        pa.IconUrl.ShouldNotBe(pb.IconUrl);
    }

    private sealed class TestHarness
    {
        public SlackPersonaBuilder Builder { get; }
        public IParticipantDisplayNameResolver NameResolver { get; }

        private TestHarness(SlackPersonaBuilder builder, IParticipantDisplayNameResolver resolver)
        {
            Builder = builder;
            NameResolver = resolver;
        }

        public static TestHarness Create()
        {
            var services = new ServiceCollection();
            var nameResolver = Substitute.For<IParticipantDisplayNameResolver>();
            services.AddSingleton(nameResolver);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            return new TestHarness(new SlackPersonaBuilder(scopeFactory), nameResolver);
        }
    }
}
