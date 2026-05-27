// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Host.Api.Services;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the server-side Hat disambiguation rule (ADR-0062 § 5
/// / #2829). Pins the four-tier priority: raw → role → unit → Guid
/// suffix.
/// </summary>
public class HatLabelDisambiguatorTests
{
    private static readonly Guid BobId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AliceId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid BobTwoId = Guid.Parse("12ab1234-0000-0000-0000-000000000001");
    private static readonly Guid BobThreeId = Guid.Parse("34cd1234-0000-0000-0000-000000000002");

    [Fact]
    public void NoCollision_RendersBaseName()
    {
        var context = new List<HatLabelCandidate>
        {
            new(BobId, "Bob", "Magazine", new[] { "designer" }),
            new(AliceId, "Alice", "Magazine", new[] { "reviewer" }),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("Alice");
    }

    [Fact]
    public void SameName_DifferentRoles_AppendsRole()
    {
        var context = new List<HatLabelCandidate>
        {
            new(BobId, "Bob", "Magazine", new[] { "designer" }),
            new(BobTwoId, "Bob", "Magazine", new[] { "reviewer" }),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob — designer");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("Bob — reviewer");
    }

    [Fact]
    public void SameName_SameRole_DifferentUnits_AppendsUnit()
    {
        var context = new List<HatLabelCandidate>
        {
            new(BobId, "Bob", "Magazine", new[] { "designer" }),
            new(BobTwoId, "Bob", "Newsletter", new[] { "designer" }),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob (Magazine)");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("Bob (Newsletter)");
    }

    [Fact]
    public void SameName_SameRole_SameUnit_AppendsGuidPrefix()
    {
        // Same name, same role, same unit → only the Guid disambiguates.
        // The 4-hex-char prefix is no-dash, matching the wire form on
        // canonical addresses.
        var context = new List<HatLabelCandidate>
        {
            new(BobTwoId, "Bob", "Magazine", new[] { "designer" }),
            new(BobThreeId, "Bob", "Magazine", new[] { "designer" }),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob #12ab");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("Bob #34cd");
    }

    [Fact]
    public void SameName_NoMemberships_AppendsGuidPrefix()
    {
        // Tenant-scoped Hats with no unit memberships collapse the
        // role and unit tiers; the Guid-suffix tier always wins.
        var context = new List<HatLabelCandidate>
        {
            new(BobTwoId, "Bob", null, null),
            new(BobThreeId, "Bob", null, null),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob #12ab");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("Bob #34cd");
    }

    [Fact]
    public void CaseInsensitiveCollision_StillDisambiguates()
    {
        // "Bob" vs "bob" are treated as colliding for the purposes
        // of the rule, matching the CLI's exact-match resolution
        // which is also case-insensitive.
        var context = new List<HatLabelCandidate>
        {
            new(BobId, "Bob", "Magazine", new[] { "designer" }),
            new(BobTwoId, "bob", "Newsletter", new[] { "designer" }),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe("Bob (Magazine)");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe("bob (Newsletter)");
    }

    [Fact]
    public void DisambiguateAll_BatchHelper_KeyedByHumanId()
    {
        var context = new List<HatLabelCandidate>
        {
            new(BobId, "Bob", "Magazine", new[] { "designer" }),
            new(BobTwoId, "Bob", "Magazine", new[] { "reviewer" }),
            new(AliceId, "Alice", "Magazine", new[] { "designer" }),
        };

        var labels = HatLabelDisambiguator.DisambiguateAll(context);

        labels[BobId].ShouldBe("Bob — designer");
        labels[BobTwoId].ShouldBe("Bob — reviewer");
        labels[AliceId].ShouldBe("Alice");
    }

    [Fact]
    public void EmptyDisplayName_NormalisesToEmptyString()
    {
        // BaseName is the value the endpoint passes (already with the
        // displayName→username fallback applied). A blank base name
        // collapses to "" and the Guid suffix still wins on collision.
        var context = new List<HatLabelCandidate>
        {
            new(BobTwoId, "  ", null, null),
            new(BobThreeId, "", null, null),
        };

        HatLabelDisambiguator.Disambiguate(context[0], context).ShouldBe(" #12ab");
        HatLabelDisambiguator.Disambiguate(context[1], context).ShouldBe(" #34cd");
    }
}
