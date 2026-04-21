// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Endpoints;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="ActivitySourceNormalizer"/> — the
/// slug-to-actor-id rewriter introduced for issue #987. The portal queries
/// activity by slug (<c>unit:portal-scratch-1</c>), but the persistence layer
/// keys on the Dapr actor id, so without normalization every slug-based query
/// returned an empty page.
/// </summary>
public class ActivitySourceNormalizerTests
{
    private const string UnitActorId = "2d3e4f56-7890-4abc-8def-0123456789ab";
    private const string AgentActorId = "00aa11bb-22cc-4dd5-e6f7-8901234567ef";

    [Fact]
    public async Task NormalizeQuerySourceAsync_UnitSlug_ResolvesToActorId()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("unit", "portal-scratch-1"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", "portal-scratch-1"),
                UnitActorId,
                "Portal Scratch 1",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            "unit:portal-scratch-1", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit:{UnitActorId}");
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_AgentSlug_ResolvesToActorId()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("agent", "ada"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "ada"),
                AgentActorId,
                "Ada",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            "agent:ada", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"agent:{AgentActorId}");
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_KnownActorId_StillRoundTripsThroughResolver()
    {
        // ResolveAsync accepts the same address shape for slug or actor id —
        // the DirectoryService persists ActorId separately and returns it
        // verbatim. This test pins the behaviour so a caller who already
        // holds the actor id doesn't regress to an empty page.
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("unit", UnitActorId), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", UnitActorId),
                UnitActorId,
                "Portal Scratch 1",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            $"unit:{UnitActorId}", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit:{UnitActorId}");
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_UnknownSlug_PassesThroughUnchanged()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // The query still runs; an unknown slug must surface as an empty
        // result page (which the downstream equality filter produces
        // naturally), not a 400.
        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            "unit:does-not-exist", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe("unit:does-not-exist");
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_UrlFormSlug_AcceptedAndResolved()
    {
        // Portal code paths that build display strings sometimes emit the
        // `://` form. Accept both shapes on the REST surface so a future
        // client that sends `unit://slug` doesn't regress to empty.
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("unit", "eng-team"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", "eng-team"),
                UnitActorId,
                "Eng Team",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            "unit://eng-team", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit:{UnitActorId}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("nocolon")]
    [InlineData(":leading-colon")]
    [InlineData("trailing-colon:")]
    public async Task NormalizeQuerySourceAsync_Malformed_PassesThrough(string? source)
    {
        var directoryService = Substitute.For<IDirectoryService>();

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            source, directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe(source);
        await directoryService.DidNotReceive()
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_NonResolvableScheme_PassesThrough()
    {
        // `human:` and `connector:` addresses never register actor ids the
        // directory owns — skip the lookup so we don't poke the resolver for
        // schemes the normalizer has nothing to rewrite.
        var directoryService = Substitute.For<IDirectoryService>();

        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            "human:alice", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe("human:alice");
        await directoryService.DidNotReceive()
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalizeStreamSourceAsync_UnitSlug_EmitsDoubleSlashWithActorId()
    {
        // The SSE filter compares against `{scheme}://{path}` — the stream
        // shape needs the URL form, not the single-colon persisted shape.
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("unit", "eng-team"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", "eng-team"),
                UnitActorId,
                "Eng Team",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeStreamSourceAsync(
            "unit:eng-team", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit://{UnitActorId}");
    }

    [Fact]
    public async Task NormalizeStreamSourceAsync_UnknownSlug_PassesThrough()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var result = await ActivitySourceNormalizer.NormalizeStreamSourceAsync(
            "agent:unknown", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe("agent:unknown");
    }
}