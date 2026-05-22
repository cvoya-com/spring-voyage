// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Agents;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Dapr.Agents;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ExecutionConfigInheritanceResolver"/> — the
/// four-branch resolver from ADR-0039 §6.
/// </summary>
/// <remarks>
/// Covers the four resolution branches:
/// <list type="bullet">
///   <item><b>Zero parents.</b> The resolver returns <c>agentOwn</c> as-is
///   with no conflicts. Tenant-default fallthrough is a known gap on the
///   default implementation; the cloud overlay supplies the tenant-aware
///   variant.</item>
///   <item><b>One parent.</b> Fields missing on <c>agentOwn</c> are filled
///   from the parent's persisted defaults; agent-set fields win.</item>
///   <item><b>N parents identical.</b> Every parent agreeing on a field
///   inherits that value with no conflict reported.</item>
///   <item><b>N parents diverging.</b> The diverging field appears on
///   <see cref="InheritanceResolution.ConflictingFields"/> with one
///   <see cref="ParentValue"/> entry per contributing parent.</item>
/// </list>
/// </remarks>
public class ExecutionConfigInheritanceResolverTests
{
    private readonly IUnitExecutionStore _store = Substitute.For<IUnitExecutionStore>();
    private readonly ExecutionConfigInheritanceResolver _resolver;
    private readonly Guid _tenantId = Guid.NewGuid();

    public ExecutionConfigInheritanceResolverTests()
    {
        _resolver = new ExecutionConfigInheritanceResolver(
            _store,
            NullLogger<ExecutionConfigInheritanceResolver>.Instance);
    }

    // ── Branch 1: zero parents ──────────────────────────────────────────────

    [Fact]
    public void ResolveAgentConfig_ZeroParents_ReturnsAgentOwnWithNoConflicts()
    {
        var agentOwn = new AgentExecutionConfig(
            Runtime: "claude",
            Image: "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
            Hosting: AgentHostingMode.Persistent,
            Model: new Model("anthropic", "claude-sonnet"));

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: Array.Empty<Guid>(),
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.ShouldBe(agentOwn);
    }

    // ── Branch 2: one parent ───────────────────────────────────────────────

    [Fact]
    public void ResolveAgentConfig_OneParent_InheritsMissingFieldsFromParent()
    {
        var parentId = Guid.NewGuid();
        StubParent(parentId, new UnitExecutionDefaults(
            Image: "unit-img:1",
            Model: new Model("openai", "gpt-4o"),
            Runtime: "spring-voyage"));

        // Agent declares only Runtime + Hosting; everything else should
        // fall through from the parent.
        var agentOwn = new AgentExecutionConfig(
            Runtime: "claude",
            Image: null,
            Hosting: AgentHostingMode.Persistent,
            Model: null);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentId },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.Runtime.ShouldBe("claude"); // agent wins
        result.Effective.Image.ShouldBe("unit-img:1");      // inherited
        result.Effective.Model.ShouldBe(new Model("openai", "gpt-4o")); // inherited
        result.Effective.Hosting.ShouldBe(AgentHostingMode.Persistent); // agent-owned
    }

    [Fact]
    public void ResolveAgentConfig_OneParent_AgentValueWinsOverParent()
    {
        var parentId = Guid.NewGuid();
        StubParent(parentId, new UnitExecutionDefaults(
            Image: "unit-img",
            Model: new Model("openai", "gpt-4o"),
            Runtime: "spring-voyage"));

        var agentOwn = new AgentExecutionConfig(
            Runtime: "claude",
            Image: "agent-img",
            Hosting: AgentHostingMode.Ephemeral,
            Model: new Model("anthropic", "claude-sonnet"));

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentId },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.Runtime.ShouldBe("claude");
        result.Effective.Image.ShouldBe("agent-img");
        result.Effective.Model.ShouldBe(new Model("anthropic", "claude-sonnet"));
    }

    // ── Branch 3: N parents identical ──────────────────────────────────────

    [Fact]
    public void ResolveAgentConfig_MultipleParentsIdentical_InheritsCommonValue()
    {
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        var common = new UnitExecutionDefaults(
            Image: "shared-img",
            Model: new Model("anthropic", "claude-sonnet"),
            Runtime: "claude");
        StubParent(parentA, common);
        StubParent(parentB, common);

        // Agent leaves every inheritable field unset.
        var agentOwn = new AgentExecutionConfig(
            Runtime: "",
            Image: null,
            Hosting: AgentHostingMode.Ephemeral,
            Model: null);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentA, parentB },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.Runtime.ShouldBe("claude");
        result.Effective.Image.ShouldBe("shared-img");
        result.Effective.Model.ShouldBe(new Model("anthropic", "claude-sonnet"));
    }

    // ── Branch 4: N parents diverging ──────────────────────────────────────

    [Fact]
    public void ResolveAgentConfig_MultipleParentsDiverging_ReportsConflictWithParentValues()
    {
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        StubParent(parentA, new UnitExecutionDefaults(Image: "img-a", Runtime: "claude"));
        StubParent(parentB, new UnitExecutionDefaults(
            Image: "img-b",       // diverges
            Runtime: "claude"));  // agrees

        var agentOwn = new AgentExecutionConfig(
            Runtime: "",
            Image: null,
            Hosting: AgentHostingMode.Ephemeral);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentA, parentB },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldContainKey(ExecutionConfigInheritanceResolver.FieldNames.Image);
        result.ConflictingFields.Count.ShouldBe(1); // only image diverged

        var imageConflicts = result.ConflictingFields[ExecutionConfigInheritanceResolver.FieldNames.Image];
        imageConflicts.Count.ShouldBe(2);
        imageConflicts.ShouldContain(new ParentValue(parentA, "img-a"));
        imageConflicts.ShouldContain(new ParentValue(parentB, "img-b"));

        // Fields that agreed are still merged; only the diverging slot is null.
        result.Effective.Runtime.ShouldBe("claude");
        result.Effective.Image.ShouldBeNull();
    }

    [Fact]
    public void ResolveAgentConfig_DivergingField_AgentExplicitValueSuppressesConflict()
    {
        var parentA = Guid.NewGuid();
        var parentB = Guid.NewGuid();
        StubParent(parentA, new UnitExecutionDefaults(Image: "img-a", Runtime: "claude"));
        StubParent(parentB, new UnitExecutionDefaults(Image: "img-b", Runtime: "claude"));

        // Agent sets Image explicitly — the per-parent disagreement is
        // moot for that field.
        var agentOwn = new AgentExecutionConfig(
            Runtime: "",
            Image: "agent-img",
            Hosting: AgentHostingMode.Ephemeral);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentA, parentB },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.Image.ShouldBe("agent-img");
        result.Effective.Runtime.ShouldBe("claude");
    }

    [Fact]
    public void ResolveAgentConfig_HostingIsNeverInheritedFromParents()
    {
        var parentId = Guid.NewGuid();
        StubParent(parentId, new UnitExecutionDefaults(Runtime: "claude"));

        var agentOwn = new AgentExecutionConfig(
            Runtime: "",
            Image: null,
            Hosting: AgentHostingMode.Persistent);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentId },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.Effective.Hosting.ShouldBe(AgentHostingMode.Persistent);
        result.ConflictingFields.ShouldNotContainKey("hosting");
    }

    [Fact]
    public void ResolveAgentConfig_ParentReturnsNullDefaults_TreatedAsEmpty()
    {
        var parentId = Guid.NewGuid();
        // No stub — Returns(default) yields a Task with null result.
        _store.GetAsync(GuidFormatter.Format(parentId), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<UnitExecutionDefaults?>(null));

        var agentOwn = new AgentExecutionConfig(
            Runtime: "claude",
            Image: "agent-img",
            Hosting: AgentHostingMode.Ephemeral);

        var result = _resolver.ResolveAgentConfig(
            agentOwn,
            parentUnitIds: new[] { parentId },
            tenantId: _tenantId,
            ct: TestContext.Current.CancellationToken);

        result.ConflictingFields.ShouldBeEmpty();
        result.Effective.Runtime.ShouldBe("claude");
        result.Effective.Image.ShouldBe("agent-img");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private void StubParent(Guid unitId, UnitExecutionDefaults defaults)
    {
        _store.GetAsync(GuidFormatter.Format(unitId), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<UnitExecutionDefaults?>(defaults));
    }
}
