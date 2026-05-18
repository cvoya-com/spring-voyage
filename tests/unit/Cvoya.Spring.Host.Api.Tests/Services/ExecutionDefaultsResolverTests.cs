// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Collections.Generic;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Pure-function tests for <see cref="ExecutionDefaultsResolver"/>
/// (#1679). Covers the field-wise merge between the package-level
/// <see cref="PackageExecutionDeclaration"/> and each member unit's own
/// <see cref="ExecutionManifest"/>, the <c>inherit:</c> matrix, and the
/// pre-flight gap surfaced when neither side declares
/// <c>execution.image</c>.
/// </summary>
public class ExecutionDefaultsResolverTests
{
    [Fact]
    public void Resolve_NoPackageExecution_NoMemberExecution_ReportsMissingImage()
    {
        var pkg = BuildPackage(
            packageExecution: null,
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.Count.ShouldBe(1);
        result.Missing[0].UnitName.ShouldBe("alpha");
        result.Missing[0].Field.ShouldBe("image");
    }

    [Fact]
    public void Resolve_NoPackageExecution_MemberHasImage_NoMissing()
    {
        var pkg = BuildPackage(
            packageExecution: null,
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      image: ghcr.io/example/alpha:latest
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/alpha:latest");
    }

    [Fact]
    public void Resolve_PackageImage_MemberNone_MemberInherits()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/agent:latest",
                Provider: null,
                Model: null,
                InheritUnits: null),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/agent:latest");
    }

    [Fact]
    public void Resolve_PackageImage_MemberOverridesImage_MemberWinsFieldwise()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/pkg:latest",
                Provider: null,
                Model: null,
                InheritUnits: null),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      image: ghcr.io/example/alpha:latest
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        // member's own image wins
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/alpha:latest");
    }

    [Fact]
    public void Resolve_PackageImage_MemberOverridesModelOnly_FieldwiseMerge()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/pkg:latest",
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                InheritUnits: null),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      model: claude-sonnet-4
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        var alpha = result.ByUnit["alpha"];
        alpha.Image.ShouldBe("ghcr.io/example/pkg:latest");      // inherited
        alpha.Provider.ShouldBe("anthropic");                      // inherited
        alpha.Model.ShouldBe("claude-sonnet-4");                   // overridden
    }

    [Fact]
    public void Resolve_InheritList_OnlyNamedMembersInherit_OthersStandalone()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/pkg:latest",
                Provider: null,
                Model: null,
                InheritUnits: new[] { "alpha" }),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    execution:
                      image: ghcr.io/example/beta:latest
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/pkg:latest");
        // beta is NOT in inherit list — its own image is the only source
        result.ByUnit["beta"].Image.ShouldBe("ghcr.io/example/beta:latest");
    }

    [Fact]
    public void Resolve_InheritList_OptedOutMemberHasNoImage_ReportsMissing()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/pkg:latest",
                Provider: null,
                Model: null,
                InheritUnits: new[] { "alpha" }),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.Count.ShouldBe(1);
        result.Missing[0].UnitName.ShouldBe("beta");
    }

    [Fact]
    public void Resolve_PackageExecutionWithoutImage_MemberHasImage_NoMissing()
    {
        // The package supplies provider / model defaults but not
        // an image. Members that supply their own image still resolve cleanly.
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: null,
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                InheritUnits: null),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    execution:
                      image: ghcr.io/example/alpha:latest
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/alpha:latest");
    }

    [Fact]
    public void Resolve_PackageProvidesEverything_AllMembersIdentical()
    {
        var pkg = BuildPackage(
            packageExecution: new PackageExecutionDeclaration(
                Image: "ghcr.io/example/agents:latest",
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                InheritUnits: null),
            unitContent: new[]
            {
                ("alpha", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: alpha
                    description: x
                    """),
                ("beta", """
                    apiVersion: spring.voyage/v1
                    kind: Unit
                    name: beta
                    description: x
                    """),
            });

        var result = ExecutionDefaultsResolver.Resolve(pkg);

        result.Missing.ShouldBeEmpty();
        result.ByUnit["alpha"].Image.ShouldBe("ghcr.io/example/agents:latest");
        result.ByUnit["beta"].Image.ShouldBe("ghcr.io/example/agents:latest");
        result.ByUnit["alpha"].Model.ShouldBe("claude-opus-4-7");
        result.ByUnit["beta"].Model.ShouldBe("claude-opus-4-7");
    }

    // ---- Helpers --------------------------------------------------------

    private static ResolvedPackage BuildPackage(
        PackageExecutionDeclaration? packageExecution,
        (string Name, string Yaml)[] unitContent)
    {
        var units = new List<ResolvedArtefact>();
        foreach (var (name, yaml) in unitContent)
        {
            units.Add(new ResolvedArtefact
            {
                Name = name,
                SourcePackage = null,
                Kind = ArtefactKind.Unit,
                ResolvedPath = null,
                Content = yaml,
            });
        }

        return new ResolvedPackage
        {
            Name = "test-pkg",
            Description = "x",
            Version = "1.0.0",
            Kind = PackageKind.UnitPackage,
            InputValues = new Dictionary<string, string>(),
            Units = units,
            Agents = System.Array.Empty<ResolvedArtefact>(),
            Skills = System.Array.Empty<ResolvedArtefact>(),
            HumanTemplates = System.Array.Empty<ResolvedArtefact>(),
            Execution = packageExecution,
        };
    }
}
