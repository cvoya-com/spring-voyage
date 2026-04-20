// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.IO;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="TenantFilteringSkillBundleResolver"/>. Verifies
/// the decorator blocks unbound / disabled bundles and falls through to
/// the inner file-system resolver when the tenant has an
/// <c>enabled=true</c> binding.
/// </summary>
public class TenantFilteringSkillBundleResolverTests : IDisposable
{
    private readonly string _root;

    public TenantFilteringSkillBundleResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spring-voyage-tests", $"resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
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
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ResolveAsync_Unbound_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        WritePackage("software-engineering", "triage.md", "## prompt");

        var binding = Substitute.For<ITenantSkillBundleBindingService>();
        binding.GetAsync("software-engineering", Arg.Any<CancellationToken>())
            .Returns((TenantSkillBundleBinding?)null);
        var sut = BuildSut(binding);

        await Should.ThrowAsync<SkillBundlePackageNotFoundException>(
            () => sut.ResolveAsync(new SkillBundleReference("software-engineering", "triage"), ct));
    }

    [Fact]
    public async Task ResolveAsync_DisabledBinding_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        WritePackage("research", "summarise.md", "## prompt");

        var binding = Substitute.For<ITenantSkillBundleBindingService>();
        binding.GetAsync("research", Arg.Any<CancellationToken>())
            .Returns(new TenantSkillBundleBinding(
                "default", "research", Enabled: false, DateTimeOffset.UtcNow));
        var sut = BuildSut(binding);

        await Should.ThrowAsync<SkillBundlePackageNotFoundException>(
            () => sut.ResolveAsync(new SkillBundleReference("research", "summarise"), ct));
    }

    [Fact]
    public async Task ResolveAsync_EnabledBinding_DelegatesToInner()
    {
        var ct = TestContext.Current.CancellationToken;
        WritePackage("software-engineering", "triage.md", "## triage prompt");

        var binding = Substitute.For<ITenantSkillBundleBindingService>();
        binding.GetAsync("software-engineering", Arg.Any<CancellationToken>())
            .Returns(new TenantSkillBundleBinding(
                "default", "software-engineering", Enabled: true, DateTimeOffset.UtcNow));
        var sut = BuildSut(binding);

        var bundle = await sut.ResolveAsync(
            new SkillBundleReference("software-engineering", "triage"), ct);

        bundle.PackageName.ShouldBe("software-engineering");
        bundle.SkillName.ShouldBe("triage");
        bundle.Prompt.ShouldContain("triage prompt");
    }

    [Fact]
    public async Task ResolveAsync_NormalisesNamespacePrefix_BeforeBindingLookup()
    {
        // A manifest entry like "spring-voyage/software-engineering"
        // should translate to a binding lookup on the package directory
        // name "software-engineering" — otherwise the default prefix
        // configuration silently diverges between the inner resolver and
        // the decorator.
        var ct = TestContext.Current.CancellationToken;
        WritePackage("software-engineering", "triage.md", "## p");

        var binding = Substitute.For<ITenantSkillBundleBindingService>();
        binding.GetAsync("software-engineering", Arg.Any<CancellationToken>())
            .Returns(new TenantSkillBundleBinding(
                "default", "software-engineering", Enabled: true, DateTimeOffset.UtcNow));
        var sut = BuildSut(binding);

        var bundle = await sut.ResolveAsync(
            new SkillBundleReference("spring-voyage/software-engineering", "triage"), ct);

        bundle.SkillName.ShouldBe("triage");
        await binding.Received(1).GetAsync(
            "software-engineering", Arg.Any<CancellationToken>());
    }

    private TenantFilteringSkillBundleResolver BuildSut(ITenantSkillBundleBindingService binding)
    {
        var inner = new FileSystemSkillBundleResolver(
            new SkillBundleOptions { PackagesRoot = _root },
            NullLogger<FileSystemSkillBundleResolver>.Instance);
        return new TenantFilteringSkillBundleResolver(
            inner,
            binding,
            NullLogger<TenantFilteringSkillBundleResolver>.Instance);
    }

    private void WritePackage(string packageDir, string skillFile, string content)
    {
        var skillsDir = Path.Combine(_root, packageDir, "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, skillFile), content);
    }
}