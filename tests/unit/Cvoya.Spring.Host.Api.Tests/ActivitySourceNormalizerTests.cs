// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Endpoints;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="ActivitySourceNormalizer"/>.
///
/// Post #1629: the normalizer's slug-to-actor-id rewrite path is gone
/// (every address is identity), so the surface that remains is
/// pass-through behaviour for malformed / non-Guid / non-resolvable
/// inputs and the canonical URL-form output for the SSE stream.
/// </summary>
public class ActivitySourceNormalizerTests
{
    private static readonly Guid UnitActorId = new("2d3e4f56-7890-4abc-8def-0123456789ab");

    [Fact]
    public async Task NormalizeQuerySourceAsync_KnownGuidActorId_ReturnsGuidPath()
    {
        // The directory resolves the Guid back to an entry whose
        // ActorId matches; the normalizer emits "unit:<no-dash>".
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
            $"unit:{UnitActorId:N}", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit:{GuidFormatter.Format(UnitActorId)}");
    }

    [Fact]
    public async Task NormalizeQuerySourceAsync_UnknownGuid_PassesThroughUnchanged()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        // Unresolved sources surface as the original input verbatim so the
        // downstream equality filter produces an empty result page rather
        // than a 400. Mirrors the pre-fix observable shape.
        var unknown = $"unit:{Guid.NewGuid():N}";
        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            unknown, directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe(unknown);
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
        // `human:` addresses are not directory-resolved — skip the lookup
        // so we don't poke the resolver for schemes the normalizer has
        // nothing to rewrite.
        var directoryService = Substitute.For<IDirectoryService>();

        var input = $"human:{Guid.NewGuid():N}";
        var result = await ActivitySourceNormalizer.NormalizeQuerySourceAsync(
            input, directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe(input);
        await directoryService.DidNotReceive()
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NormalizeStreamSourceAsync_KnownGuid_EmitsDoubleSlashUrlForm()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(new Address("unit", UnitActorId), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", UnitActorId),
                UnitActorId,
                "Eng Team",
                string.Empty,
                Role: null,
                DateTimeOffset.UtcNow));

        var result = await ActivitySourceNormalizer.NormalizeStreamSourceAsync(
            $"unit:{UnitActorId:N}", directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe($"unit://{GuidFormatter.Format(UnitActorId)}");
    }

    [Fact]
    public async Task NormalizeStreamSourceAsync_UnknownGuid_PassesThrough()
    {
        var directoryService = Substitute.For<IDirectoryService>();
        directoryService.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var unknown = $"agent:{Guid.NewGuid():N}";
        var result = await ActivitySourceNormalizer.NormalizeStreamSourceAsync(
            unknown, directoryService, TestContext.Current.CancellationToken);

        result.ShouldBe(unknown);
    }
}
