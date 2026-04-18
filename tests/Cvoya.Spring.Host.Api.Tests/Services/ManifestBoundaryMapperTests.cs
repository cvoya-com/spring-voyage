// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Reflection;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Proves the manifest → core projection (used by <c>spring apply</c>) and
/// the HTTP DTO → core projection (used by the boundary endpoints) produce
/// the same <see cref="UnitBoundary"/> record for equivalent inputs. That
/// is the "round-trip test: YAML in → UnitDefinitions.Definition JSON
/// persisted → boundary behaves identically to API-set boundary" acceptance
/// criterion of #494 — if the two projections agree, the filtering /
/// projection / synthesis path downstream cannot observe a difference.
/// </summary>
public class ManifestBoundaryMapperTests
{
    // ManifestBoundaryMapper is internal to Cvoya.Spring.Host.Api; we go
    // through reflection rather than adding InternalsVisibleTo just for
    // this one test.
    private static UnitBoundary InvokeMapper(BoundaryManifest manifest)
    {
        var hostApi = System.Reflection.Assembly.Load("Cvoya.Spring.Host.Api");
        var type = hostApi.GetType("Cvoya.Spring.Host.Api.Services.ManifestBoundaryMapper")!;
        var method = type.GetMethod(
            "ToCore",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)!;
        return (UnitBoundary)method.Invoke(null, new object?[] { manifest })!;
    }

    [Fact]
    public void ManifestAndHttpDtoProject_ToEquivalentUnitBoundary()
    {
        // Same set of rules, expressed in both surfaces.
        var manifest = new BoundaryManifest
        {
            Opacities = new()
            {
                new BoundaryOpacityManifestEntry { DomainPattern = "internal-*" },
                new BoundaryOpacityManifestEntry { OriginPattern = "agent://secret" },
            },
            Projections = new()
            {
                new BoundaryProjectionManifestEntry
                {
                    DomainPattern = "backend-*",
                    RenameTo = "engineering",
                    Retag = "team view",
                    OverrideLevel = "advanced",
                },
            },
            Syntheses = new()
            {
                new BoundarySynthesisManifestEntry
                {
                    Name = "full-stack",
                    DomainPattern = "*",
                    Description = "team-level full-stack",
                    Level = "expert",
                },
            },
        };

        var httpDto = new UnitBoundaryResponse(
            Opacities: new List<BoundaryOpacityRuleDto>
            {
                new("internal-*", null),
                new(null, "agent://secret"),
            },
            Projections: new List<BoundaryProjectionRuleDto>
            {
                new("backend-*", null, "engineering", "team view", "advanced"),
            },
            Syntheses: new List<BoundarySynthesisRuleDto>
            {
                new("full-stack", "*", null, "team-level full-stack", "expert"),
            });

        var fromManifest = InvokeMapper(manifest);
        var fromHttp = httpDto.ToCore();

        // Compare slot-by-slot. Using value equality on the inner records so
        // any drift in the projection surfaces as a clear inequality.
        fromManifest.Opacities!.ShouldBe(fromHttp.Opacities);
        fromManifest.Projections!.ShouldBe(fromHttp.Projections);
        fromManifest.Syntheses!.ShouldBe(fromHttp.Syntheses);
    }

    [Fact]
    public void BlankSynthesisName_DroppedIdenticallyOnBothPaths()
    {
        var manifest = new BoundaryManifest
        {
            Syntheses = new()
            {
                new BoundarySynthesisManifestEntry { Name = null },
                new BoundarySynthesisManifestEntry { Name = "" },
                new BoundarySynthesisManifestEntry { Name = "valid-one" },
            },
        };
        var httpDto = new UnitBoundaryResponse(
            Syntheses: new List<BoundarySynthesisRuleDto>
            {
                new(null!, null, null, null, null),
                new("", null, null, null, null),
                new("valid-one", null, null, null, null),
            });

        var fromManifest = InvokeMapper(manifest);
        var fromHttp = httpDto.ToCore();

        fromManifest.Syntheses!.Count.ShouldBe(1);
        fromHttp.Syntheses!.Count.ShouldBe(1);
        fromManifest.Syntheses![0].Name.ShouldBe("valid-one");
        fromHttp.Syntheses![0].Name.ShouldBe("valid-one");
    }

    [Fact]
    public void UnknownLevel_ResolvesToNullIdenticallyOnBothPaths()
    {
        var manifest = new BoundaryManifest
        {
            Projections = new()
            {
                new BoundaryProjectionManifestEntry
                {
                    DomainPattern = "x",
                    OverrideLevel = "grand-wizard",
                },
            },
        };
        var httpDto = new UnitBoundaryResponse(
            Projections: new List<BoundaryProjectionRuleDto>
            {
                new("x", null, null, null, "grand-wizard"),
            });

        var fromManifest = InvokeMapper(manifest);
        var fromHttp = httpDto.ToCore();

        fromManifest.Projections![0].OverrideLevel.ShouldBeNull();
        fromHttp.Projections![0].OverrideLevel.ShouldBeNull();
    }

    [Fact]
    public void AllLevelsParseCaseInsensitively()
    {
        // Every documented level should round-trip through the manifest
        // mapper — matching the HTTP DTO so operators can copy values
        // verbatim between the two surfaces.
        foreach (var level in new[] { "beginner", "INTERMEDIATE", "Advanced", "eXpErT" })
        {
            var manifest = new BoundaryManifest
            {
                Syntheses = new()
                {
                    new BoundarySynthesisManifestEntry { Name = "x", Level = level },
                },
            };
            var core = InvokeMapper(manifest);
            core.Syntheses![0].Level.ShouldNotBeNull($"level '{level}' should parse");
        }
    }
}