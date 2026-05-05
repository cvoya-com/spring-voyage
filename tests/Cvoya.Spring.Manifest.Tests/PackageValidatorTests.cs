// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Manifest.Validation;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the offline <see cref="PackageValidator"/> shipped with #1680.
/// Each test materialises a synthetic package on a temp directory and runs
/// the validator through <see cref="DirectoryPackageSource"/>. The live
/// packages in <c>packages/</c> have their own test below
/// (<see cref="LivePackagesValidateClean"/>) — those are the regression
/// tests the CI gate exists to enforce.
/// </summary>
public class PackageValidatorTests : IDisposable
{
    private readonly string _root;

    public PackageValidatorTests()
    {
        _root = Directory.CreateTempSubdirectory("sv-package-validator-").FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string LivePackageRoot(string packageName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SpringVoyage.slnx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate solution root.");
        }
        return Path.Combine(dir.FullName, "packages", packageName);
    }

    // ── live-package regression tests ────────────────────────────────────

    [Theory]
    [InlineData("research")]
    [InlineData("product-management")]
    [InlineData("software-engineering")]
    [InlineData("spring-voyage-oss")]
    public async Task LivePackagesValidateClean(string packageName)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new DirectoryPackageSource(LivePackageRoot(packageName));
        var result = await PackageValidator.ValidateAsync(source, ct);

        // Errors break the install. Warnings indicate drift but not breakage.
        // The CI gate runs --strict so warnings are also blocked at merge time;
        // here we assert errors == 0 so a warning surfaces in a future PR
        // as a single targeted failure rather than a sea of noise.
        result.ErrorCount.ShouldBe(
            0,
            customMessage: "Live package '" + packageName + "' has errors:\n" +
                string.Join("\n", result.Diagnostics.Select(d => $"  {d.File}: {d.Severity} {d.Code} {d.Message}")));
    }

    // ── synthetic broken-package tests ────────────────────────────────────

    [Fact]
    public async Task UnitMissingExecutionImage_IsError()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              execution:
                runtime: podman
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "units/u.yaml" &&
            d.Severity == PackageValidationSeverity.Error &&
            d.Code == "unit-missing-image");
    }

    [Fact]
    public async Task AgentMissingModel_IsError()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - agent: a
            """);
        Write("agents/a.yaml", """
            agent:
              id: a
              ai:
                agent: claude
                tool: claude-code
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "agents/a.yaml" &&
            d.Severity == PackageValidationSeverity.Error &&
            d.Code == "agent-missing-model");
    }

    [Fact]
    public async Task DanglingMemberAgentReference_IsError()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              members:
                - agent: nonexistent
              execution:
                image: localhost/foo:latest
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "units/u.yaml" &&
            d.Severity == PackageValidationSeverity.Error &&
            d.Code == "unit-member-agent-not-found");
    }

    [Fact]
    public async Task DanglingMemberUnitReference_IsError()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              members:
                - unit: missing-sub
              execution:
                image: localhost/foo:latest
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "units/u.yaml" &&
            d.Severity == PackageValidationSeverity.Error &&
            d.Code == "unit-member-unit-not-found");
    }

    [Fact]
    public async Task GuidMemberReference_IsAcceptedAsCrossPackage()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - unit: u
            """);
        // 32-char no-dash hex Guid form — the production cross-package shape.
        Write("units/u.yaml", """
            unit:
              name: u
              members:
                - agent: 0123456789abcdef0123456789abcdef
              execution:
                image: localhost/foo:latest
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldNotContain(d => d.Code == "unit-member-agent-not-found");
        result.Diagnostics.ShouldNotContain(d => d.Code == "unit-member-unit-not-found");
    }

    [Fact]
    public async Task UnknownConnectorSlug_IsWarning()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              execution:
                image: localhost/foo:latest
              connectors:
                - type: imaginary-slug
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "units/u.yaml" &&
            d.Severity == PackageValidationSeverity.Warning &&
            d.Code == "connector-unknown-slug");
        result.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public async Task UndeclaredInputInterpolation_IsError()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: pkg
            inputs:
              - name: declared_input
                type: string
                required: true
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              execution:
                image: localhost/foo:latest
              connectors:
                - type: github
                  config:
                    owner: ${{ inputs.undeclared_input }}
                    repo: ${{ inputs.declared_input }}
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        // Exactly one undeclared-input diagnostic, naming the right input.
        var inputDiags = result.Diagnostics.Where(d => d.Code == "input-undeclared").ToList();
        inputDiags.Count.ShouldBe(1);
        inputDiags[0].Message.ShouldContain("'undeclared_input'");
        inputDiags[0].Message.ShouldNotContain("'declared_input'");
    }

    [Fact]
    public async Task MissingPackageYaml_IsError()
    {
        // Empty directory — no package.yaml.
        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.Diagnostics.ShouldContain(d =>
            d.File == "package.yaml" &&
            d.Severity == PackageValidationSeverity.Error &&
            d.Code == "package-yaml-missing");
    }

    [Fact]
    public async Task CleanMinimalPackage_HasNoDiagnostics()
    {
        Write("package.yaml", """
            apiVersion: spring.voyage/v1
            metadata:
              name: clean
            content:
              - unit: u
            """);
        Write("units/u.yaml", """
            unit:
              name: u
              execution:
                image: localhost/foo:latest
                runtime: podman
            """);

        var result = await PackageValidator.ValidateAsync(
            new DirectoryPackageSource(_root),
            TestContext.Current.CancellationToken);

        result.IsClean.ShouldBeTrue();
        result.Diagnostics.ShouldBeEmpty();
        result.Files.ShouldContain("package.yaml");
        result.Files.ShouldContain("units/u.yaml");
    }
}