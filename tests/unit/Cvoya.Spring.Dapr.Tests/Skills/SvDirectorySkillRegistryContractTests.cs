// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Contract tests for <see cref="SvDirectorySkillRegistry"/> — pin the tool
/// surface, the no-context-overload rejection, the unknown-tool rejection,
/// and the tenant-sentinel-warning text in the user-facing tool descriptions
/// (the LLM consuming the description must understand the sentinel is
/// non-addressable). Wider behaviour (graph traversal, expertise inclusion,
/// authz denial paths) is covered by integration tests that wire a real
/// SpringDbContext / member graph.
/// </summary>
public class SvDirectorySkillRegistryContractTests
{
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IUnitMemberGraphStore _memberGraphStore = new InMemoryUnitMemberGraphStore();
    private readonly IUnitHumanMembershipStore _humanMembershipStore =
        new InMemoryUnitHumanMembershipStore();
    private readonly IUnitMemberRoleDirectory _memberRoleDirectory =
        new InMemoryUnitMemberRoleDirectory();
    private readonly IExpertiseStore _expertiseStore = Substitute.For<IExpertiseStore>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvDirectorySkillRegistryContractTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tenantContext.CurrentTenantId.Returns(Guid.NewGuid());
    }

    private SvDirectorySkillRegistry CreateRegistry() => new(
        _scopeFactory,
        _memberGraphStore,
        _humanMembershipStore,
        _memberRoleDirectory,
        _expertiseStore,
        _actorProxyFactory,
        _tenantContext,
        _loggerFactory);

    [Fact]
    public void GetToolDefinitions_AdvertisesEveryDirectoryTool()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolDefinitions();

        // ADR-0056 §8: sv.directory.list / sv.directory.lookup join the
        // surface alongside the existing navigation + status tools. Order is
        // stable so callers caching tool slots see new tools appended after
        // the navigation set.
        tools.Select(t => t.Name).ShouldBe(new[]
        {
            SvDirectorySkillRegistry.GetSelfTool,
            SvDirectorySkillRegistry.GetMemberTool,
            SvDirectorySkillRegistry.ListMembersTool,
            SvDirectorySkillRegistry.GetSiblingsTool,
            SvDirectorySkillRegistry.GetParentsTool,
            SvDirectorySkillRegistry.GetStatusTool,
            SvDirectorySkillRegistry.ListTool,
            SvDirectorySkillRegistry.LookupTool,
        });
    }

    [Fact]
    public void GetToolDefinitions_EveryDirectoryToolCarriesTheDirectoryCategory()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().ShouldAllBe(
            t => t.Category == ToolCategories.Directory,
            "the discovery surface (ADR-0056 §6 / #2656) consults ToolDefinition.Category to " +
            "group tools; every sv.directory.* tool must self-classify under 'directory'.");
    }

    [Fact]
    public void GetToolDefinitions_RegistryIdentifiesAsSv()
    {
        var registry = CreateRegistry();
        registry.Name.ShouldBe("sv");
    }

    [Fact]
    public void GetToolDefinitions_TenantSentinelWarningPresentOnGetParentsAndGetMember()
    {
        var registry = CreateRegistry();
        var byName = registry.GetToolDefinitions().ToDictionary(t => t.Name, t => t.Description);

        // The tenant entry can surface from get_parents (top-level walk)
        // and get_member (caller passes the tenant uuid). Both descriptions
        // MUST tell the LLM the sentinel is non-addressable so the model
        // doesn't try to message or delegate to it. Pin the load-bearing
        // phrasing so a future doc edit can't silently drop the warning.
        byName[SvDirectorySkillRegistry.GetParentsTool].ShouldContain("kind='tenant'");
        byName[SvDirectorySkillRegistry.GetParentsTool].ShouldContain("NOT addressable");
        byName[SvDirectorySkillRegistry.GetMemberTool].ShouldContain("kind='tenant'");
        byName[SvDirectorySkillRegistry.GetMemberTool].ShouldContain("NOT addressable");
    }

    [Fact]
    public void GetToolDefinitions_PaginationDocumentedOnListAndSiblings()
    {
        var registry = CreateRegistry();
        var byName = registry.GetToolDefinitions().ToDictionary(t => t.Name, t => t);

        byName[SvDirectorySkillRegistry.ListMembersTool].Description.ShouldContain("limit");
        byName[SvDirectorySkillRegistry.ListMembersTool].Description.ShouldContain("offset");
        byName[SvDirectorySkillRegistry.GetSiblingsTool].Description.ShouldContain("limit");
        byName[SvDirectorySkillRegistry.GetSiblingsTool].Description.ShouldContain("offset");
    }

    [Fact]
    public async Task NoContextInvokeAsync_AlwaysThrows()
    {
        // sv.* tools depend on caller identity for every operation
        // (get_self obviously, but every other tool's authz path keys on
        // CallerId). Direct callers that hit the legacy no-context overload
        // would silently lose the caller — fail loud instead.
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;

        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(SvDirectorySkillRegistry.GetSelfTool, args, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;
        var ctx = new ToolCallContext(
            CallerId: Guid.NewGuid().ToString("N"),
            CallerKind: "agent",
            ThreadId: Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("sv.does_not_exist", args, ctx, TestContext.Current.CancellationToken));
    }
}
