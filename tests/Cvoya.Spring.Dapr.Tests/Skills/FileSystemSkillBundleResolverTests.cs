// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.IO;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="FileSystemSkillBundleResolver"/>. Covers the resolver
/// surface enumerated in #167: happy-path prompt + tools, prompt-only bundle,
/// unknown package, unknown skill, namespace-prefix stripping, and traversal
/// safety.
/// </summary>
public class FileSystemSkillBundleResolverTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemSkillBundleResolver _resolver;

    public FileSystemSkillBundleResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spring-voyage-tests", $"bundles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var options = new SkillBundleOptions
        {
            PackagesRoot = _root,
        };
        _resolver = new FileSystemSkillBundleResolver(
            options,
            NullLogger<FileSystemSkillBundleResolver>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public async Task ResolveAsync_PromptAndToolsFile_ReturnsParsedBundle()
    {
        WriteSkill("software-engineering", "triage-and-assign",
            "## Triage prompt text",
            """
            [
              {
                "name": "assignToAgent",
                "description": "Assign a work item",
                "parameters": { "type": "object", "required": ["agentId"], "properties": { "agentId": { "type": "string" } } }
              }
            ]
            """);

        var bundle = await _resolver.ResolveAsync(
            new SkillBundleReference("spring-voyage/software-engineering", "triage-and-assign"),
            TestContext.Current.CancellationToken);

        bundle.PackageName.ShouldBe("spring-voyage/software-engineering");
        bundle.SkillName.ShouldBe("triage-and-assign");
        bundle.Prompt.ShouldContain("Triage prompt text");
        bundle.RequiredTools.Count.ShouldBe(1);
        bundle.RequiredTools[0].Name.ShouldBe("assignToAgent");
        bundle.RequiredTools[0].Description.ShouldBe("Assign a work item");
        bundle.RequiredTools[0].Optional.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_NoToolsFile_ReturnsEmptyToolList()
    {
        WriteSkill("software-engineering", "prompt-only", "## Prompt-only skill", toolsJson: null);

        var bundle = await _resolver.ResolveAsync(
            new SkillBundleReference("spring-voyage/software-engineering", "prompt-only"),
            TestContext.Current.CancellationToken);

        bundle.Prompt.ShouldContain("Prompt-only");
        bundle.RequiredTools.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_UnknownPackage_Throws()
    {
        var ex = await Should.ThrowAsync<SkillBundlePackageNotFoundException>(
            () => _resolver.ResolveAsync(
                new SkillBundleReference("spring-voyage/does-not-exist", "x"),
                TestContext.Current.CancellationToken));

        ex.PackageName.ShouldBe("spring-voyage/does-not-exist");
        ex.Message.ShouldContain("does-not-exist");
    }

    [Fact]
    public async Task ResolveAsync_UnknownSkill_Throws()
    {
        // Package exists, skill does not.
        Directory.CreateDirectory(Path.Combine(_root, "software-engineering", "skills"));

        var ex = await Should.ThrowAsync<SkillBundleNotFoundException>(
            () => _resolver.ResolveAsync(
                new SkillBundleReference("spring-voyage/software-engineering", "missing-skill"),
                TestContext.Current.CancellationToken));

        ex.SkillName.ShouldBe("missing-skill");
        ex.PackageName.ShouldBe("spring-voyage/software-engineering");
    }

    [Fact]
    public async Task ResolveAsync_UsesCacheOnSecondCall()
    {
        WriteSkill("software-engineering", "cacheable", "## Cached", toolsJson: null);

        var first = await _resolver.ResolveAsync(
            new SkillBundleReference("spring-voyage/software-engineering", "cacheable"),
            TestContext.Current.CancellationToken);
        var second = await _resolver.ResolveAsync(
            new SkillBundleReference("spring-voyage/software-engineering", "cacheable"),
            TestContext.Current.CancellationToken);

        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_UnprefixedPackageName_Works()
    {
        WriteSkill("software-engineering", "triage", "## Triage", toolsJson: null);

        var bundle = await _resolver.ResolveAsync(
            new SkillBundleReference("software-engineering", "triage"),
            TestContext.Current.CancellationToken);

        bundle.SkillName.ShouldBe("triage");
    }

    [Fact]
    public async Task ResolveAsync_RootNotConfigured_Throws()
    {
        var resolverNoRoot = new FileSystemSkillBundleResolver(
            new SkillBundleOptions { PackagesRoot = null },
            NullLogger<FileSystemSkillBundleResolver>.Instance);

        await Should.ThrowAsync<SkillBundlePackageNotFoundException>(
            () => resolverNoRoot.ResolveAsync(
                new SkillBundleReference("p", "s"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_OptionalToolFlagIsParsed()
    {
        WriteSkill("software-engineering", "with-optional",
            "## Skill",
            """
            [
              { "name": "required", "description": "d", "parameters": {} },
              { "name": "extra", "description": "d", "parameters": {}, "optional": true }
            ]
            """);

        var bundle = await _resolver.ResolveAsync(
            new SkillBundleReference("software-engineering", "with-optional"),
            TestContext.Current.CancellationToken);

        bundle.RequiredTools.Count.ShouldBe(2);
        bundle.RequiredTools.First(t => t.Name == "required").Optional.ShouldBeFalse();
        bundle.RequiredTools.First(t => t.Name == "extra").Optional.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_TraversalPackageName_Throws()
    {
        await Should.ThrowAsync<SkillBundlePackageNotFoundException>(
            () => _resolver.ResolveAsync(
                new SkillBundleReference("..", "x"),
                TestContext.Current.CancellationToken));
    }

    private void WriteSkill(string packageDir, string skill, string promptMarkdown, string? toolsJson)
    {
        var skillsDir = Path.Combine(_root, packageDir, "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, skill + ".md"), promptMarkdown);
        if (toolsJson is not null)
        {
            File.WriteAllText(Path.Combine(skillsDir, skill + ".tools.json"), toolsJson);
        }
    }
}