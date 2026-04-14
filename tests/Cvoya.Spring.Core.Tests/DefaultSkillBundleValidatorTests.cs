// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DefaultSkillBundleValidator"/>. Covers:
/// happy-path (all tools available, no policy), missing tool (validation
/// problem), policy-blocked tool, optional-missing-tool tolerance, and the
/// empty-bundle no-op.
/// </summary>
public class DefaultSkillBundleValidatorTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task Validate_EmptyBundleList_NoOp()
    {
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository());

        await validator.ValidateAsync("engineering", Array.Empty<SkillBundle>(), TestContext.Current.CancellationToken);
        // No throw == pass.
    }

    [Fact]
    public async Task Validate_ToolAvailable_NoPolicy_Passes()
    {
        var bundle = BundleWith("triage-and-assign", "assignToAgent");
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "assignToAgent") },
            new FakePolicyRepository());

        await validator.ValidateAsync("engineering", new[] { bundle }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Validate_MissingRequiredTool_Throws()
    {
        var bundle = BundleWith("triage-and-assign", "assignToAgent");
        var validator = new DefaultSkillBundleValidator(
            Array.Empty<ISkillRegistry>(),
            new FakePolicyRepository());

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync("engineering", new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.ShouldHaveSingleItem();
        ex.Problems[0].ToolName.ShouldBe("assignToAgent");
        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.ToolNotAvailable);
    }

    [Fact]
    public async Task Validate_MissingOptionalTool_Passes()
    {
        var bundle = new SkillBundle(
            PackageName: "p", SkillName: "s", Prompt: "",
            RequiredTools: new[]
            {
                new SkillToolRequirement("mustHave", "", EmptySchema, Optional: false),
                new SkillToolRequirement("niceToHave", "", EmptySchema, Optional: true),
            });

        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "mustHave") },
            new FakePolicyRepository());

        await validator.ValidateAsync("u", new[] { bundle }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Validate_ToolBlockedByUnitPolicy_Throws()
    {
        var bundle = BundleWith("triage-and-assign", "assignToAgent");
        var policy = new UnitPolicy(new SkillPolicy(Blocked: new[] { "assignToAgent" }));
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "assignToAgent") },
            FakePolicyRepository.With(("engineering", policy)));

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync("engineering", new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems.ShouldHaveSingleItem();
        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.BlockedByUnitPolicy);
        ex.Problems[0].DenyingUnitId.ShouldBe("engineering");
    }

    [Fact]
    public async Task Validate_ToolNotInWhitelist_Throws()
    {
        var bundle = BundleWith("triage-and-assign", "assignToAgent");
        var policy = new UnitPolicy(new SkillPolicy(Allowed: new[] { "search" }));
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "assignToAgent") },
            FakePolicyRepository.With(("engineering", policy)));

        var ex = await Should.ThrowAsync<SkillBundleValidationException>(
            () => validator.ValidateAsync("engineering", new[] { bundle }, TestContext.Current.CancellationToken));

        ex.Problems[0].Reason.ShouldBe(SkillBundleValidationProblemReason.BlockedByUnitPolicy);
    }

    [Fact]
    public async Task Validate_CaseInsensitiveToolMatch()
    {
        var bundle = BundleWith("triage-and-assign", "AssignToAgent");
        var validator = new DefaultSkillBundleValidator(
            new[] { new FakeRegistry("platform", "assignToAgent") },
            new FakePolicyRepository());

        await validator.ValidateAsync("u", new[] { bundle }, TestContext.Current.CancellationToken);
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
            toolNames.Select(n => new ToolDefinition(n, $"desc {n}", EmptySchema)).ToList();

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePolicyRepository : IUnitPolicyRepository
    {
        private readonly Dictionary<string, UnitPolicy> _rows = new(StringComparer.Ordinal);

        public static FakePolicyRepository With(params (string unit, UnitPolicy policy)[] rows)
        {
            var repo = new FakePolicyRepository();
            foreach (var (unit, policy) in rows)
            {
                repo._rows[unit] = policy;
            }
            return repo;
        }

        public Task<UnitPolicy> GetAsync(string unitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.TryGetValue(unitId, out var p) ? p : UnitPolicy.Empty);

        public Task SetAsync(string unitId, UnitPolicy policy, CancellationToken cancellationToken = default)
        {
            _rows[unitId] = policy;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string unitId, CancellationToken cancellationToken = default)
        {
            _rows.Remove(unitId);
            return Task.CompletedTask;
        }
    }
}