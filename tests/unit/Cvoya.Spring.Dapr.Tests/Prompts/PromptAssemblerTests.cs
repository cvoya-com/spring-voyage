// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PromptAssembler"/>.
/// </summary>
public class PromptAssemblerTests
{
    private readonly IPlatformPromptProvider _platformProvider = Substitute.For<IPlatformPromptProvider>();
    private readonly UnitContextBuilder _unitContextBuilder = new();
    private readonly AgentInstructionsBuilder _agentInstructionsBuilder = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly PromptAssembler _assembler;

    public PromptAssemblerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _platformProvider.GetPlatformPromptAsync(Arg.Any<CancellationToken>())
            .Returns("Platform safety rules.");
        _assembler = new PromptAssembler(
            _platformProvider,
            _unitContextBuilder,
            _agentInstructionsBuilder,
            _loggerFactory);
    }

    /// <summary>
    /// Verifies that the three active sections (platform instructions, unit
    /// context, role-specific instructions) are included in order when
    /// context is set.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_IncludesAllSectionsInOrder()
    {
        var context = new PromptAssemblyContext(
            Policies: JsonSerializer.SerializeToElement(new { maxRetries = 3 }),
            AgentInstructions: "You are a code reviewer.");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldContain("## Unit Context");
        result.ShouldContain(PromptAssembler.RoleSpecificInstructionsHeading);

        // Verify ordering
        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var unitIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var roleIdx = result.IndexOf(PromptAssembler.RoleSpecificInstructionsHeading, StringComparison.Ordinal);

        platformIdx.ShouldBeLessThan(unitIdx);
        unitIdx.ShouldBeLessThan(roleIdx);
    }

    /// <summary>
    /// Verifies that empty sections are omitted gracefully.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_OmitsEmptySectionsGracefully()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: null);

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldNotContain("## Unit Context");
        result.ShouldNotContain(PromptAssembler.RoleSpecificInstructionsHeading);
    }

    /// <summary>
    /// Verifies that calling with no context at all produces just the
    /// platform-instructions section.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_NullContext_OnlyPlatformInstructions()
    {
        var result = await _assembler.AssembleAsync(context: null, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldNotContain("## Unit Context");
        result.ShouldNotContain(PromptAssembler.RoleSpecificInstructionsHeading);
    }

    /// <summary>
    /// Pins the #2670 acceptance: the per-registry skill listing was
    /// removed from the unit-context section. Even a context that names a
    /// unit policy must not synthesise an "Available Skills" sub-section,
    /// and the auto-generated "Tools exposed by the X connector." string
    /// must be impossible to surface because the projection that produced
    /// it no longer exists.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_DoesNotEmitAvailableSkillsBlockInUnitContext()
    {
        var context = new PromptAssemblyContext(
            Policies: JsonSerializer.SerializeToElement(new { maxRetries = 3 }),
            AgentInstructions: null);

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Unit Context");
        result.ShouldNotContain("Available Skills");
        result.ShouldNotContain("Tools exposed by the");
    }

    /// <summary>
    /// Unit-equipped skill bundles render in the unit-context section;
    /// agent-equipped bundles render in the role-specific instructions
    /// section. The same SkillBundle going through the agent slot must
    /// land under the role-specific heading, not under "## Unit Context",
    /// so member-agent inheritance (unit → unit-context section) does not
    /// conflate with the agent's own bundles.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_UnitBundles_RenderInUnitContextSection()
    {
        var bundle = new SkillBundle(
            PackageName: "spring-voyage/software-engineering",
            SkillName: "triage-and-assign",
            Prompt: "## Triage prompt body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: null,
            SkillBundles: new[] { bundle });

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Unit Context");
        var unitIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var bundleIdx = result.IndexOf("triage-and-assign", StringComparison.Ordinal);
        bundleIdx.ShouldBeGreaterThan(unitIdx);
        result.ShouldNotContain(PromptAssembler.RoleSpecificInstructionsHeading);
    }

    [Fact]
    public async Task AssembleAsync_AgentBundles_RenderInRoleSpecificInstructionsSection()
    {
        var bundle = new SkillBundle(
            PackageName: "spring-voyage/software-engineering",
            SkillName: "pr-review-cycle",
            Prompt: "## PR Review prompt body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: null,
            AgentSkillBundles: new[] { bundle });

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain(PromptAssembler.RoleSpecificInstructionsHeading);
        var roleIdx = result.IndexOf(PromptAssembler.RoleSpecificInstructionsHeading, StringComparison.Ordinal);
        var bundleIdx = result.IndexOf("pr-review-cycle", StringComparison.Ordinal);
        bundleIdx.ShouldBeGreaterThan(roleIdx);

        // Must not leak the bundle body into the unit-context section —
        // the two paths are strictly separated even when both feature
        // only the agent bundles slot.
        result.ShouldNotContain("## Unit Context");
    }

    /// <summary>
    /// The assembler renders an auto-injected "Connector context" sub-
    /// section between the platform-contract body and the unit-context
    /// section when the per-invocation context carries connector prompt
    /// fragments. Per #2738 the section header is a <c>###</c> sub-
    /// section of <c>## Platform Instructions</c>, and each contributor
    /// fragment opens with a <c>####</c> sub-heading naming the
    /// binding so the heading tree stays clean.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_ConnectorPromptFragments_RenderUnderPlatformSection()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: new[]
            {
                "#### GitHub binding — cvoya-com/spring-voyage\nbody-a",
                "#### Other binding\nbody-b",
            });

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("### Connector context (auto-injected by platform)");
        result.ShouldContain("#### GitHub binding — cvoya-com/spring-voyage");
        result.ShouldContain("#### Other binding");
        result.ShouldContain("body-a");
        result.ShouldContain("body-b");

        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var connectorIdx = result.IndexOf("### Connector context", StringComparison.Ordinal);
        var roleIdx = result.IndexOf(PromptAssembler.RoleSpecificInstructionsHeading, StringComparison.Ordinal);
        platformIdx.ShouldBeLessThan(connectorIdx);
        connectorIdx.ShouldBeLessThan(roleIdx);
    }

    [Fact]
    public async Task AssembleAsync_NoConnectorPromptFragments_OmitsConnectorSectionEntirely()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldNotContain("Connector context");
    }

    [Fact]
    public async Task AssembleAsync_NullAndBlankConnectorFragments_AreSkipped()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: null,
            ConnectorPromptFragments: new[]
            {
                "#### Real fragment\nbody",
                string.Empty,
                "   ",
            });

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("#### Real fragment");
        // The whitespace-only entries do not produce any extra heading-
        // looking output; the section heading appears exactly once.
        var firstSection = result.IndexOf("### Connector context", StringComparison.Ordinal);
        var secondSection = result.IndexOf("### Connector context", firstSection + 1, StringComparison.Ordinal);
        secondSection.ShouldBe(-1);
    }

    [Fact]
    public async Task AssembleAsync_UnitAndAgentBundles_RenderInDistinctSections()
    {
        var unitBundle = new SkillBundle(
            PackageName: "pkg-u",
            SkillName: "skill-u",
            Prompt: "unit-bundle-body",
            RequiredTools: Array.Empty<SkillToolRequirement>());
        var agentBundle = new SkillBundle(
            PackageName: "pkg-a",
            SkillName: "skill-a",
            Prompt: "agent-bundle-body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "user-instructions",
            SkillBundles: new[] { unitBundle },
            AgentSkillBundles: new[] { agentBundle });

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        var unitCtxIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var roleIdx = result.IndexOf(PromptAssembler.RoleSpecificInstructionsHeading, StringComparison.Ordinal);
        var unitBundleIdx = result.IndexOf("skill-u", StringComparison.Ordinal);
        var agentBundleIdx = result.IndexOf("skill-a", StringComparison.Ordinal);
        var instructionsIdx = result.IndexOf("user-instructions", StringComparison.Ordinal);

        unitCtxIdx.ShouldBeGreaterThanOrEqualTo(0);
        roleIdx.ShouldBeGreaterThan(unitCtxIdx);

        // Unit bundle renders inside the unit-context section; agent
        // bundle + user instructions render inside the role-specific
        // instructions section — and the instructions appear before the
        // bundle subsection within that section.
        unitBundleIdx.ShouldBeInRange(unitCtxIdx, roleIdx);
        instructionsIdx.ShouldBeGreaterThan(roleIdx);
        agentBundleIdx.ShouldBeGreaterThan(instructionsIdx);
    }

    /// <summary>
    /// #2680: the pre-rendered identity fragment lands in the
    /// platform-instructions section, after the platform contract body
    /// and before the connector-context subsection. The assembler does
    /// not wrap or rewrite the fragment — it owns its own heading. Per
    /// #2738 the resolver renders <c>### Who you are</c> so it nests
    /// under <c>## Platform Instructions</c>.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_IdentityPromptFragment_RendersAfterPlatformBeforeConnectorContext()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: new[] { "#### A binding\nbody-a" },
            IdentityPromptFragment: "### Who you are\n- **Kind:** agent");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain("### Who you are");
        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var identityIdx = result.IndexOf("### Who you are", StringComparison.Ordinal);
        var connectorIdx = result.IndexOf("### Connector context", StringComparison.Ordinal);

        platformIdx.ShouldBeLessThan(identityIdx);
        identityIdx.ShouldBeLessThan(connectorIdx);
    }

    [Fact]
    public async Task AssembleAsync_NullIdentityPromptFragment_OmitsSectionEntirely()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldNotContain("Who you are");
    }

    /// <summary>
    /// #2682: the launcher-contributed workspace fragment lands in the
    /// platform-instructions section under a fixed
    /// <c>### Container and workspace</c> heading (level updated per
    /// #2738 so it nests under <c>## Platform Instructions</c>) owned
    /// by the assembler. Rendered after identity and before connector
    /// context so all platform-injected sections live together.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_WorkspacePromptFragment_RendersUnderFixedHeading()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: new[] { "#### A binding\nbody-a" },
            IdentityPromptFragment: "### Who you are\n- **Kind:** agent",
            WorkspacePromptFragment: "You are running inside a Debian-based container.");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldContain(PromptAssembler.ContainerAndWorkspaceHeading);
        result.ShouldContain("You are running inside a Debian-based container.");

        var identityIdx = result.IndexOf("### Who you are", StringComparison.Ordinal);
        var workspaceIdx = result.IndexOf(PromptAssembler.ContainerAndWorkspaceHeading, StringComparison.Ordinal);
        var connectorIdx = result.IndexOf("### Connector context", StringComparison.Ordinal);

        identityIdx.ShouldBeLessThan(workspaceIdx);
        workspaceIdx.ShouldBeLessThan(connectorIdx);
    }

    [Fact]
    public async Task AssembleAsync_NullWorkspacePromptFragment_OmitsSectionEntirely()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body");

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldNotContain(PromptAssembler.ContainerAndWorkspaceHeading);
    }

    /// <summary>
    /// #2684: the role-specific instructions heading is kind-neutral —
    /// no "agent" or "unit" wording — so unit-shaped subjects see the
    /// same heading as agent-shaped subjects.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_RoleSpecificInstructionsHeading_IsKindNeutral()
    {
        PromptAssembler.RoleSpecificInstructionsHeading.ShouldBe("## Role-specific instructions");
        PromptAssembler.RoleSpecificInstructionsHeading.ShouldNotContain("Agent");
        PromptAssembler.RoleSpecificInstructionsHeading.ShouldNotContain("Unit");
    }

    // ────────────────────────────────────────────────────────────────────
    // #2738 — snapshot pins for the new structure
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// #2738: the runtime guard renders <em>inside</em>
    /// <c>## Platform Instructions</c> as a <c>### …</c> sub-section
    /// when the per-invocation context sets
    /// <c>ConcurrentThreadsGuard: true</c>. Pre-#2738 the guard was
    /// prepended above the assembled prompt; that broke the heading
    /// tree and put platform-emitted guidance above the section it
    /// belongs to.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_ConcurrentThreadsGuard_RendersInsidePlatformInstructions()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConcurrentThreadsGuard: true);

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        // The prompt starts with `## Platform Instructions`; the guard
        // sub-heading appears strictly after that top-level heading
        // and strictly before the next top-level (`## Role-specific
        // instructions`) heading.
        result.ShouldStartWith(PromptAssembler.PlatformInstructionsHeading);
        result.ShouldContain("### " + Cvoya.Spring.Core.Execution.LauncherPromptFragments.ConcurrentThreadsGuardAnchor);

        var platformIdx = result.IndexOf(PromptAssembler.PlatformInstructionsHeading, StringComparison.Ordinal);
        var guardIdx = result.IndexOf(
            "### " + Cvoya.Spring.Core.Execution.LauncherPromptFragments.ConcurrentThreadsGuardAnchor,
            StringComparison.Ordinal);
        var roleIdx = result.IndexOf(PromptAssembler.RoleSpecificInstructionsHeading, StringComparison.Ordinal);

        guardIdx.ShouldBeGreaterThan(platformIdx);
        guardIdx.ShouldBeLessThan(roleIdx);
    }

    /// <summary>
    /// #2738: when the per-invocation context does NOT set the
    /// concurrent-threads flag (synthetic launches, tests that build
    /// a sparse context) the assembler does not surface the guard.
    /// This keeps the default behaviour predictable for callers that
    /// have not yet been wired through the cascade.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_ConcurrentThreadsGuardFalse_OmitsGuardEntirely()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConcurrentThreadsGuard: false);

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldNotContain(Cvoya.Spring.Core.Execution.LauncherPromptFragments.ConcurrentThreadsGuardAnchor);
    }

    /// <summary>
    /// #2738 acceptance: the legacy <c>## End Spring Voyage runtime
    /// guard</c> closing heading does not survive in the output —
    /// markdown section boundaries are implicit.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_DoesNotEmitEndOfGuardClosingHeading()
    {
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConcurrentThreadsGuard: true);

        var result = await _assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        result.ShouldNotContain("End Spring Voyage runtime guard");
        result.ShouldNotContain("## End ");
    }

    /// <summary>
    /// #2738 acceptance: the headings form a single tree. The only
    /// top-level (<c>##</c>) sections are <c>## Platform Instructions</c>,
    /// <c>## Unit Context</c>, and <c>## Role-specific instructions</c>;
    /// every platform-emitted sub-section appears at <c>###</c> (or
    /// deeper for the per-binding connector entries). Pre-#2738 the
    /// platform body emitted <c>## About Spring Voyage</c>,
    /// <c>## Inbound messages</c>, <c>## Connector context</c>, etc. as
    /// siblings of their own parent — this pin keeps the regression
    /// from coming back.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_HeadingHierarchy_FormsSingleTree()
    {
        // Use the production PlatformPromptProvider so the body's actual
        // heading levels are pinned, not the mock's stubbed string.
        var assembler = new PromptAssembler(
            new PlatformPromptProvider(),
            _unitContextBuilder,
            _agentInstructionsBuilder,
            _loggerFactory);

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: new[] { "#### GitHub binding — owner/repo\nhint" },
            IdentityPromptFragment: "### Who you are\n- **Kind:** agent",
            WorkspacePromptFragment: "You are running inside a container.",
            ConcurrentThreadsGuard: true);

        var result = await assembler.AssembleAsync(context, TestContext.Current.CancellationToken);

        // No `#` (h1) headings — we have not adopted a single-title scheme.
        foreach (var line in result.Split('\n'))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Unexpected `#` heading in the assembled prompt:\n  {line}");
            }
        }

        // The only `##` headings are the three top-level sections.
        var topLevel = result
            .Split('\n')
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .ToList();
        topLevel.ShouldBe(new[]
        {
            PromptAssembler.PlatformInstructionsHeading,
            PromptAssembler.RoleSpecificInstructionsHeading,
        }, ignoreOrder: false);

        // Every platform-emitted sub-section appears at level 3.
        var subHeadings = result
            .Split('\n')
            .Where(l => l.StartsWith("### ", StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        subHeadings.ShouldContain("### About Spring Voyage");
        subHeadings.ShouldContain("### Platform Contract — Non-Negotiable");
        subHeadings.ShouldContain("### Inbound messages");
        subHeadings.ShouldContain("### " + Cvoya.Spring.Core.Execution.LauncherPromptFragments.ConcurrentThreadsGuardAnchor);
        subHeadings.ShouldContain("### Who you are");
        subHeadings.ShouldContain(PromptAssembler.ContainerAndWorkspaceHeading);
        subHeadings.ShouldContain(PromptAssembler.ConnectorContextHeading);

        // The per-binding GitHub fragment renders at level 4 under
        // `### Connector context …` so the connector-section tree stays
        // intact regardless of how many bindings the subject inherits.
        result.ShouldContain("#### GitHub binding — owner/repo");

        // The legacy bracketed markers do not survive in any rendering.
        result.ShouldNotContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        result.ShouldNotContain("[END PLATFORM CONTRACT]");
        result.ShouldNotContain("End Spring Voyage runtime guard");
    }
}
