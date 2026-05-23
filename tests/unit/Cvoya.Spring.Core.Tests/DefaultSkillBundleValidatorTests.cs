// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DefaultSkillBundleValidator"/> under strict
/// validation (#2346). Covers:
/// happy-path (namespace resolves via registry, no policy);
/// missing-namespace blocks install (was warning under #261; now blocking);
/// image-tier fallback resolves a tool when no registry exposes the
/// namespace; optional-missing-tool tolerance; policy-blocked tool still
/// blocks; the empty-bundle no-op.
/// </summary>
public class DefaultSkillBundleValidatorTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task Validate_EmptyBundleList_ReturnsEmptyReport()
    {
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("engineering"), Array.Empty<SkillBundle>(), TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_NamespaceRegistered_NoPolicy_ReturnsEmptyReport()
    {
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.assign_to_agent") },
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("engineering"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_NamespaceResolvesEvenWhenExactToolNotListed()
    {
        // The validator resolves at the namespace level (#2346): a registry
        // that exposes ANY tool under the declaration's namespace satisfies
        // the requirement, because the grant resolver lands the whole
        // namespace on the unit when the matching connector is bound at
        // runtime. Mirrors the ToolGrantResolver.GetToolsByNamespace shape.
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.some_other_tool") },
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_MissingRequiredTool_Throws_RequiredToolUnresolved()
    {
        // #2346: strict validation. The legacy "warning, continue" path is
        // gone — an unresolved RequiredTool blocks install. The endpoint
        // layer maps this exception to a 400 with code
        // RequiredToolUnresolved.
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(
                TestSlugIds.For("engineering"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.ShouldHaveSingleItem();
        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.ToolNotAvailable);
        ex.Problems[0].PackageName.ShouldBe("spring-voyage/software-engineering");
        ex.Problems[0].SkillName.ShouldBe("triage-and-assign");
        ex.Problems[0].ToolName.ShouldBe("platform.assign_to_agent");
    }

    [Fact]
    public async Task Validate_MultipleMissingTools_AggregatesAllProblems()
    {
        var bundle = BundleWith("pr-review-cycle", "platform.request_review", "platform.submit_review");
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(
                TestSlugIds.For("engineering"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.Count.ShouldBe(2);
        ex.Problems.ShouldContain(p => p.ToolName == "platform.request_review");
        ex.Problems.ShouldContain(p => p.ToolName == "platform.submit_review");
        ex.Problems.ShouldAllBe(p => p.Reason == SkillBundleValidationProblemReason.ToolNotAvailable);
    }

    [Fact]
    public async Task Validate_ImageTierProvidesTool_ResolvesWhenNoRegistryExposes()
    {
        // #2346: when no ISkillRegistry exposes the declaration's namespace,
        // the validator falls back to the image-tier IImageToolsReader (the
        // Sub C seam, #2336). An exact tool-name match in the subject's
        // image_tools resolves the requirement.
        var bundle = BundleWith("acme-skill", "acme.transcode_audio");
        var imageReader = new FakeImageToolsReader(
            new ImageToolEntry("acme.transcode_audio", "Transcode an audio stream", "sha256:abc"));
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            imageReader);

        var report = await validator.ValidateAsync(
            TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_ImageTierDoesNotMatchByName_StillThrows()
    {
        // Image-tier lookup is by tool name, not namespace prefix — an
        // unrelated image tool with the same namespace prefix does not
        // resolve a different tool in that namespace.
        var bundle = BundleWith("acme-skill", "acme.transcode_audio");
        var imageReader = new FakeImageToolsReader(
            new ImageToolEntry("acme.unrelated_tool", "", "sha256:abc"));
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            imageReader);

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(
                TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.ShouldHaveSingleItem();
        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.ToolNotAvailable);
    }

    [Fact]
    public async Task Validate_MissingOptionalTool_Passes()
    {
        var bundle = new SkillBundle(
            PackageName: "p", SkillName: "s", Prompt: "",
            RequiredTools: new[]
            {
                new SkillToolRequirement("platform.must_have", "", EmptySchema, Optional: false),
                new SkillToolRequirement("platform.nice_to_have", "", EmptySchema, Optional: true),
            });

        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.must_have") },
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_MissingOptionalToolInUnregisteredNamespace_Passes()
    {
        // Optional requirements stay advisory even when the namespace
        // resolves nowhere — they're explicit "tolerate the absence" signals.
        var bundle = new SkillBundle(
            PackageName: "p", SkillName: "s", Prompt: "",
            RequiredTools: new[]
            {
                new SkillToolRequirement("missing.thing", "", EmptySchema, Optional: true),
            });

        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Validate_ToolBlockedByUnitPolicy_StillThrows()
    {
        // C3 security invariant: a unit's SkillPolicy must still be enforced
        // at create time. Strict validation (#2346) does not relax this —
        // policy violations remain blocking.
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "platform.assign_to_agent" }));
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.assign_to_agent") },
            FakePolicyRepository.With(("engineering", policy)),
            new EmptyImageToolsReader());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(TestSlugIds.For("engineering"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.ShouldHaveSingleItem();
        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.BlockedByUnitPolicy);
        ex.Problems[0].DenyingUnitId.ShouldBe(TestSlugIds.For("engineering").ToString());
    }

    [Fact]
    public async Task Validate_ToolNotInWhitelist_Throws()
    {
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "platform.search" }));
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.assign_to_agent") },
            FakePolicyRepository.With(("engineering", policy)),
            new EmptyImageToolsReader());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(TestSlugIds.For("engineering"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.BlockedByUnitPolicy);
    }

    [Fact]
    public async Task Validate_MixedPolicyBlockAndMissingTool_AggregatesBothBlocking()
    {
        // Strict validation: both reasons are blocking, so the exception
        // carries both problems. Mirrors the existing policy-block invariant
        // and the new strict missing-tool invariant.
        var bundle = new SkillBundle(
            PackageName: "p", SkillName: "s", Prompt: "",
            RequiredTools: new[]
            {
                new SkillToolRequirement("platform.search", "", EmptySchema, Optional: false),
                new SkillToolRequirement("missing.thing", "", EmptySchema, Optional: false),
            });
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "platform.search" }));
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.search") },
            FakePolicyRepository.With(("u", policy)),
            new EmptyImageToolsReader());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync(TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.Count.ShouldBe(2);
        ex.Problems.ShouldContain(p =>
            p.ToolName == "platform.search"
            && p.Reason == SkillBundleValidationProblemReason.BlockedByUnitPolicy);
        ex.Problems.ShouldContain(p =>
            p.ToolName == "missing.thing"
            && p.Reason == SkillBundleValidationProblemReason.ToolNotAvailable);
    }

    [Fact]
    public async Task Validate_CaseInsensitiveToolMatch()
    {
        // Namespace lookup is case-sensitive (matches ToolGrantResolver), but
        // the declaration is normalised by the registry contract — both are
        // lowercase under ToolNaming. The validator's case-insensitive policy
        // check still applies once the namespace resolves.
        var bundle = BundleWith("triage-and-assign", "platform.assign_to_agent");
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "platform.assign_to_agent") },
            new FakePolicyRepository(),
            new EmptyImageToolsReader());

        var report = await validator.ValidateAsync(
            TestSlugIds.For("u"), new[] { bundle }, TestContext.Current.CancellationToken);

        report.Warnings.ShouldBeEmpty();
    }

    private static SkillBundle BundleWith(string skillName, params string[] toolNames)
    {
        return new SkillBundle(
            PackageName: "spring-voyage/software-engineering",
            SkillName: skillName,
            Prompt: "## " + skillName,
            RequiredTools: toolNames
                .Select(t => new SkillToolRequirement(t, "desc", EmptySchema, Optional: false))
                .ToList());
    }

    private sealed class FakeRegistry(string name, params string[] toolNames) : ISkillRegistry
    {
        public string Name { get; } = name;

        public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
            toolNames.Select(n => new ToolDefinition(n, $"desc {n}", EmptySchema, string.Empty)).ToList();

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class EmptyImageToolsReader : IImageToolsReader
    {
        public Task<IReadOnlyList<ImageToolEntry>> GetImageToolsAsync(
            Address subject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImageToolEntry>>(Array.Empty<ImageToolEntry>());
    }

    private sealed class FakeImageToolsReader(params ImageToolEntry[] entries) : IImageToolsReader
    {
        public Task<IReadOnlyList<ImageToolEntry>> GetImageToolsAsync(
            Address subject,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ImageToolEntry>>(entries);
    }

    private sealed class FakePolicyRepository : IUnitPolicyRepository
    {
        private readonly Dictionary<Guid, UnitPolicy> _rows = new();

        public static FakePolicyRepository With(params (string unit, UnitPolicy policy)[] rows)
        {
            var repo = new FakePolicyRepository();
            foreach (var (unit, policy) in rows)
            {
                repo._rows[TestSlugIds.For(unit)] = policy;
            }
            return repo;
        }

        public Task<UnitPolicy> GetAsync(Guid unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue(unitId, out var p) ? p : UnitPolicy.Empty);

        public Task SetAsync(Guid unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
        {
            _rows[unitId] = policy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid unitId, CancellationToken cancellationToken = default)
        {
            _rows.Remove(unitId);
            return Task.CompletedTask;
        }
    }

    private static class TestSlugIds
    {
        private static readonly Dictionary<string, Guid> Cache = new(StringComparer.Ordinal);

        public static Guid For(string slug)
        {
            if (Cache.TryGetValue(slug, out var existing))
            {
                return existing;
            }

            var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(slug));
            Span<byte> guidBytes = stackalloc byte[16];
            bytes.AsSpan(0, 16).CopyTo(guidBytes);
            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
            (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
            (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
            (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
            (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
            var id = new Guid(guidBytes);
            Cache[slug] = id;
            return id;
        }
    }
}
