// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Capabilities;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Capabilities;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DbExpertiseSeedProvider.ExtractExpertise"/> — the pure
/// JSON-to-<see cref="ExpertiseDomain"/> projection. DB integration is covered
/// indirectly by the integration tests that round-trip through
/// <c>OnActivateAsync</c>. See #488.
/// </summary>
public class DbExpertiseSeedProviderTests
{
    [Fact]
    public void ExtractExpertise_Null_ReturnsNull()
    {
        DbExpertiseSeedProvider.ExtractExpertise(null).ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_NoExpertiseProperty_ReturnsNull()
    {
        var doc = JsonSerializer.SerializeToElement(new { instructions = "do things" });
        DbExpertiseSeedProvider.ExtractExpertise(doc).ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_EmptyArray_ReturnsEmpty()
    {
        var doc = JsonSerializer.SerializeToElement(new { expertise = Array.Empty<object>() });
        var result = DbExpertiseSeedProvider.ExtractExpertise(doc);
        result.ShouldNotBeNull();
        result!.Count.ShouldBe(0);
    }

    [Fact]
    public void ExtractExpertise_DomainAndLevel_MapsCorrectly()
    {
        // Mirrors the user-facing YAML grammar: `- domain: X\n  level: expert`.
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[]
            {
                new { domain = "python/fastapi", level = "expert" },
                new { domain = "react/nextjs", level = "advanced" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("python/fastapi");
        result[0].Level.ShouldBe(ExpertiseLevel.Expert);
        result[1].Name.ShouldBe("react/nextjs");
        result[1].Level.ShouldBe(ExpertiseLevel.Advanced);
    }

    [Fact]
    public void ExtractExpertise_NameKey_AlsoAccepted()
    {
        // Wire-shape key spelling (`name`) must round-trip too so a dump from
        // GET /api/v1/agents/{id}/expertise can be replayed through a
        // definition file.
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[]
            {
                new { name = "architecture", description = "system design", level = "expert" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("architecture");
        result[0].Description.ShouldBe("system design");
        result[0].Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public void ExtractExpertise_MissingLevel_YieldsNullLevel()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "coding" } },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result[0].Level.ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_UnknownLevel_IgnoresLevel()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "coding", level = "wizard" } },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result.Count.ShouldBe(1);
        result[0].Level.ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_BlankDomain_SkipsEntry()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new object[]
            {
                new { domain = "", level = "expert" },
                new { domain = "ok", level = "expert" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("ok");
    }

    [Fact]
    public void ExtractExpertise_NonArrayExpertise_ReturnsNull()
    {
        var doc = JsonSerializer.SerializeToElement(new { expertise = "not-an-array" });
        DbExpertiseSeedProvider.ExtractExpertise(doc).ShouldBeNull();
    }
}