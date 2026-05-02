// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ArtefactReference.Parse"/>.
/// </summary>
public class ArtefactReferenceTests
{
    [Theory]
    [InlineData("sv-oss-design", ArtefactKind.Unit, null, "sv-oss-design", false)]
    [InlineData("architect", ArtefactKind.Agent, null, "architect", false)]
    [InlineData("code-review", ArtefactKind.Skill, null, "code-review", false)]
    [InlineData("ci-workflow", ArtefactKind.Workflow, null, "ci-workflow", false)]
    public void Parse_BareReference_IsWithinPackage(
        string raw, ArtefactKind kind, string? expectedPkg, string expectedName, bool isCross)
    {
        var r = ArtefactReference.Parse(raw, kind);

        r.RawValue.ShouldBe(raw);
        r.PackageName.ShouldBe(expectedPkg);
        r.ArtefactName.ShouldBe(expectedName);
        r.Kind.ShouldBe(kind);
        r.IsCrossPackage.ShouldBe(isCross);
    }

    [Theory]
    [InlineData("spring-voyage-oss/architect", ArtefactKind.Agent, "spring-voyage-oss", "architect", true)]
    [InlineData("research/triage", ArtefactKind.Unit, "research", "triage", true)]
    [InlineData("other-pkg/code-review", ArtefactKind.Skill, "other-pkg", "code-review", true)]
    [InlineData("analytics/ci-workflow", ArtefactKind.Workflow, "analytics", "ci-workflow", true)]
    public void Parse_QualifiedReference_IsCrossPackage(
        string raw, ArtefactKind kind, string expectedPkg, string expectedName, bool isCross)
    {
        var r = ArtefactReference.Parse(raw, kind);

        r.RawValue.ShouldBe(raw);
        r.PackageName.ShouldBe(expectedPkg);
        r.ArtefactName.ShouldBe(expectedName);
        r.IsCrossPackage.ShouldBe(isCross);
    }

    [Fact]
    public void Parse_EmptyString_Throws()
    {
        var act = () => ArtefactReference.Parse("", ArtefactKind.Unit);
        Should.Throw<PackageParseException>(act);
    }

    [Fact]
    public void Parse_WhitespaceOnly_Throws()
    {
        var act = () => ArtefactReference.Parse("   ", ArtefactKind.Unit);
        Should.Throw<PackageParseException>(act);
    }

    [Fact]
    public void Parse_TooManySegments_Throws()
    {
        var act = () => ArtefactReference.Parse("a/b/c", ArtefactKind.Unit);
        Should.Throw<PackageParseException>(act)
            .Message.ShouldContain("one '/' separator");
    }

    [Fact]
    public void Parse_NullValue_Throws()
    {
        var act = () => ArtefactReference.Parse(null!, ArtefactKind.Unit);
        Should.Throw<ArgumentNullException>(act);
    }
}