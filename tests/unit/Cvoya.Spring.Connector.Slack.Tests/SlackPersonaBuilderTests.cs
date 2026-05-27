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
    [Fact]
    public async Task ResolveAsync_AgentAddress_PullsDisplayNameFromResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        var bob = new Address(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
        harness.NameResolver.ResolveAsync(bob.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Bob"));

        var persona = await harness.Builder.ResolveAsync(bob, ct);

        persona.Username.ShouldBe("Bob");
        persona.IconUrl.ShouldStartWith(SlackPersonaBuilder.PlaceholderIconBaseUrl);
        persona.IconUrl.ShouldContain("d=identicon");
    }

    [Fact]
    public async Task ResolveAsync_UnitAddress_PullsDisplayNameFromResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        var research = new Address(Address.UnitScheme, new Guid("00000002-0000-0000-0000-000000000000"));
        harness.NameResolver.ResolveAsync(research.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Research"));

        var persona = await harness.Builder.ResolveAsync(research, ct);

        persona.Username.ShouldBe("Research");
    }

    [Fact]
    public async Task ResolveAsync_HumanAddress_PullsDisplayNameFromResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();
        var morgan = new Address(Address.HumanScheme, new Guid("00000003-0000-0000-0000-000000000000"));
        harness.NameResolver.ResolveAsync(morgan.ToString(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Morgan"));

        var persona = await harness.Builder.ResolveAsync(morgan, ct);

        persona.Username.ShouldBe("Morgan");
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
