// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultIdentityPromptContextResolver"/> —
/// the OSS-default <see cref="IIdentityPromptContextResolver"/> that
/// renders a <c>### Who you are</c> sub-section from the agent-
/// definition projection (#2680, heading level per #2738).
/// </summary>
public class DefaultIdentityPromptContextResolverTests
{
    private readonly IAgentDefinitionProvider _agentDefinitionProvider =
        Substitute.For<IAgentDefinitionProvider>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DefaultIdentityPromptContextResolver _resolver;

    public DefaultIdentityPromptContextResolverTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _resolver = new DefaultIdentityPromptContextResolver(_agentDefinitionProvider, _loggerFactory);
    }

    [Fact]
    public async Task ResolveAsync_AgentSubject_RendersKindAddressAndDisplayName()
    {
        var subjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(GuidFormatter.Format(subjectId), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "Reviewer",
                Instructions: null,
                Execution: null));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldStartWith("### Who you are");
        fragment.ShouldContain("**Kind:** agent");
        fragment.ShouldContain($"**Address:** `agent:{GuidFormatter.Format(subjectId)}`");
        fragment.ShouldContain("**Display name:** Reviewer");
    }

    [Fact]
    public async Task ResolveAsync_UnitSubject_RendersUnitKind()
    {
        var subjectId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var subject = new Address(Address.UnitScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(GuidFormatter.Format(subjectId), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "Spring Voyage OSS",
                Instructions: null,
                Execution: null,
                // For a unit-as-agent the projection still sets UnitId
                // to the unit's own id. The resolver must NOT render
                // "Parent unit: yourself".
                UnitId: GuidFormatter.Format(subjectId)));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldStartWith("### Who you are");
        fragment.ShouldContain("**Kind:** unit");
        fragment.ShouldContain("Spring Voyage OSS");
        fragment.ShouldNotContain("**Parent unit:**");
    }

    [Fact]
    public async Task ResolveAsync_AgentWithParentUnit_RendersParentUnitAddress()
    {
        var subjectId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var parentUnitId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(GuidFormatter.Format(subjectId), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "engineer-1",
                Instructions: null,
                Execution: null,
                UnitId: GuidFormatter.Format(parentUnitId)));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain($"**Parent unit:** `unit:{GuidFormatter.Format(parentUnitId)}`");
    }

    [Fact]
    public async Task ResolveAsync_PointsAtDirectoryListForLiveMemberDiscovery()
    {
        var subjectId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "x",
                Instructions: null,
                Execution: null));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        // #2741: role and expertise render inline (covered by their own
        // tests below), so the resolver no longer points at
        // sv.directory.lookup for that data. The remaining pointer is
        // sv.directory.list — the live membership / sibling /
        // peer-discovery surface.
        fragment.ShouldContain("sv.directory.list");
        fragment.ShouldNotContain(
            "sv.directory.lookup",
            customMessage: "role/expertise render inline (#2741); no use-site reminder for sv.directory.lookup belongs in the identity section.");
    }

    [Fact]
    public async Task ResolveAsync_AgentWithDeclaredRole_RendersInlineBullet()
    {
        var subjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "engineer-1",
                Instructions: null,
                Execution: null,
                Role: "software-engineer"));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain("**Role:** software-engineer");
    }

    [Fact]
    public async Task ResolveAsync_AgentWithDeclaredExpertise_RendersInlineBullets()
    {
        var subjectId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "engineer-1",
                Instructions: null,
                Execution: null,
                Expertise: new[]
                {
                    new ExpertiseDomain("dotnet-development", "Backend / Dapr / domain code.", ExpertiseLevel.Expert),
                    new ExpertiseDomain("testing", "xUnit / FluentAssertions patterns.", ExpertiseLevel.Advanced),
                }));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain("**Expertise:**");
        fragment.ShouldContain("dotnet-development (expert)");
        fragment.ShouldContain("Backend / Dapr / domain code.");
        fragment.ShouldContain("testing (advanced)");
        fragment.ShouldContain("xUnit / FluentAssertions patterns.");
    }

    [Fact]
    public async Task ResolveAsync_AgentWithoutRoleOrExpertise_OmitsThoseBullets()
    {
        var subjectId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "minimal-agent",
                Instructions: null,
                Execution: null));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldNotContain("**Role:**");
        fragment.ShouldNotContain("**Expertise:**");
    }

    [Fact]
    public async Task ResolveAsync_ExpertiseWithoutLevelOrDescription_RendersDomainNameOnly()
    {
        var subjectId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(
                AgentId: GuidFormatter.Format(subjectId),
                Name: "x",
                Instructions: null,
                Execution: null,
                Expertise: new[]
                {
                    new ExpertiseDomain("triage", string.Empty),
                }));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain("- triage");
        fragment.ShouldNotContain("triage ()");
        fragment.ShouldNotContain("triage — ");
    }

    [Fact]
    public async Task ResolveAsync_MissingDefinition_ReturnsNull()
    {
        var subjectId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var subject = new Address(Address.AgentScheme, subjectId);
        _agentDefinitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentDefinition?)null);

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_UnsupportedScheme_ReturnsNull()
    {
        var subject = new Address(Address.HumanScheme, Guid.Parse("77777777-7777-7777-7777-777777777777"));

        var fragment = await _resolver.ResolveAsync(subject, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }
}
