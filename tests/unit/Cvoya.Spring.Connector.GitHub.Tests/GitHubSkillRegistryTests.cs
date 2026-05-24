// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="GitHubSkillRegistry"/> — the
/// connector-tier <see cref="ISkillRegistry"/> that exposes
/// <c>github.get_installation_token</c> per issue #2704.
/// </summary>
public class GitHubSkillRegistryTests
{
    private static readonly Guid AgentId = new("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid UnitId = new("bbbbbbbb-1111-2222-3333-444444444444");

    [Fact]
    public void RegistryName_IsTheGithubConnectorNamespace()
    {
        var registry = BuildRegistry();
        registry.Name.ShouldBe("github");
    }

    [Fact]
    public void GetToolDefinitions_ExposesExactlyTheTwoPinnedTools()
    {
        var registry = BuildRegistry();
        var tools = registry.GetToolDefinitions();

        // Two pinned exceptions to the wider #2384 / #2383 rule:
        // get_installation_token (#2704) and describe_inbound_contract (#2676).
        // Both live in the github namespace; only describe_inbound_contract
        // participates in the platform's category-discovery surface.
        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(new[]
        {
            GitHubSkillRegistry.DescribeInboundContractTool,
            GitHubSkillRegistry.GetInstallationTokenTool,
        });
        tools.ShouldAllBe(t => t.Namespace == "github");
    }

    [Fact]
    public void TokenToolDefinition_HasObjectSchemaWithNoRequiredProperties()
    {
        var registry = BuildRegistry();
        var tool = registry.GetToolDefinitions()
            .Single(t => t.Name == GitHubSkillRegistry.GetInstallationTokenTool);

        tool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
        tool.InputSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        tool.InputSchema.GetProperty("properties").GetRawText().ShouldBe("{}");
        // The token-fetch tool is reached by name once the grant pipeline
        // surfaces it; it deliberately stays outside the category taxonomy
        // (matches the ArxivSkillRegistry precedent).
        tool.Category.ShouldBe(string.Empty);
    }

    [Fact]
    public void TokenToolDescription_ExplicitlyDisclaimsHttpFetch()
    {
        // The tool's description is part of the model-facing surface: it
        // must steer the agent away from the URL-construction shape that
        // triggered the hallucination cascade in #2704.
        var registry = BuildRegistry();
        var tool = registry.GetToolDefinitions()
            .Single(t => t.Name == GitHubSkillRegistry.GetInstallationTokenTool);

        tool.Description.ShouldContain("do NOT construct");
        tool.Description.ShouldContain("$SPRING_CONNECTOR_GITHUB_TOKEN");
    }

    [Fact]
    public void DescribeInboundContractTool_LivesInConnectorCategoryWithEmptySchema()
    {
        // #2676: surfaces via sv.tools.list_categories under "connector:github".
        var registry = BuildRegistry();
        var tool = registry.GetToolDefinitions()
            .Single(t => t.Name == GitHubSkillRegistry.DescribeInboundContractTool);

        tool.Category.ShouldBe(GitHubSkillRegistry.ConnectorCategory);
        tool.InputSchema.GetProperty("type").GetString().ShouldBe("object");
        tool.InputSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        tool.InputSchema.GetProperty("properties").GetRawText().ShouldBe("{}");
        tool.Description.ShouldContain("'source' is 'github'");
        tool.Description.ShouldContain("payload.intent");
    }

    [Fact]
    public async Task DescribeInboundContractTool_ReturnsEnvelopeAndIntents_ConsistentWithVocabulary()
    {
        var registry = BuildRegistry();
        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.DescribeInboundContractTool,
            ParseJson("{}"),
            AgentContext(AgentId),
            TestContext.Current.CancellationToken);

        var envelopeFields = result.GetProperty("envelope").GetProperty("fields");
        envelopeFields.GetArrayLength().ShouldBe(GitHubIntentVocabulary.EnvelopeFields.Count);
        envelopeFields.EnumerateArray()
            .Select(f => f.GetProperty("name").GetString())
            .ShouldBe(GitHubIntentVocabulary.EnvelopeFields.Select(f => f.Name));

        var intents = result.GetProperty("intents");
        intents.GetArrayLength().ShouldBe(GitHubIntentVocabulary.All.Count);
        intents.EnumerateArray()
            .Select(i => i.GetProperty("token").GetString())
            .ShouldBe(GitHubIntentVocabulary.All.Select(i => i.Token));

        // Every (event, action) the webhook handler can dispatch through
        // GitHubIntentVocabulary.MapAction MUST be reachable from the
        // published contract — defence against the vocabulary drifting
        // away from the webhook handler.
        var publishedActions = intents.EnumerateArray()
            .SelectMany(i => i.GetProperty("github_actions").EnumerateArray()
                .Select(a => a.GetString()!))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var intent in GitHubIntentVocabulary.All)
        {
            foreach (var src in intent.GithubActions)
            {
                publishedActions.ShouldContain(src);
            }
        }
    }

    [Fact]
    public async Task DescribeInboundContractTool_ReachableWithoutCallerContext()
    {
        // The contract has no per-caller variability — the context-less
        // overload must succeed so callers reaching the tool through that
        // path get the same document.
        var registry = BuildRegistry();
        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.DescribeInboundContractTool,
            ParseJson("{}"),
            TestContext.Current.CancellationToken);

        result.GetProperty("envelope").GetProperty("fields").GetArrayLength().ShouldBeGreaterThan(0);
        result.GetProperty("intents").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GitHubIntentVocabulary_MapAction_RoundTripsEveryPublishedAction()
    {
        // Single-source-of-truth check: every (event, action) the vocabulary
        // publishes must round-trip through MapAction back to the same intent
        // token. If a developer edits the vocab without updating MapAction
        // (or vice versa), this test fails.
        foreach (var intent in GitHubIntentVocabulary.All)
        {
            foreach (var src in intent.GithubActions)
            {
                var dot = src.IndexOf('.', StringComparison.Ordinal);
                dot.ShouldBeGreaterThan(0,
                    $"Source action '{src}' must be in 'event.action' form.");
                var @event = src[..dot];
                var action = src[(dot + 1)..];
                GitHubIntentVocabulary.MapAction(@event, action).ShouldBe(intent.Token);
            }
        }
    }

    [Fact]
    public void GetToolsByNamespace_GithubMatchesBothPinnedTools()
    {
        // GetToolsByNamespace is a default interface method on
        // ISkillRegistry; C# requires the call through the interface.
        ISkillRegistry registry = BuildRegistry();
        var tools = registry.GetToolsByNamespace("github");
        tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(new[]
        {
            GitHubSkillRegistry.DescribeInboundContractTool,
            GitHubSkillRegistry.GetInstallationTokenTool,
        });
    }

    [Fact]
    public void GetToolsByNamespace_SvDoesNotMatch()
    {
        // Defence in depth on the connector-vs-platform tier decision —
        // the tool is in the github namespace, NOT sv.*. A platform-tier
        // name would auto-grant to every agent regardless of binding.
        ISkillRegistry registry = BuildRegistry();
        registry.GetToolsByNamespace("sv").ShouldBeEmpty();
    }

    [Fact]
    public async Task InvokeAsync_WithoutCallerContext_ThrowsSpringException()
    {
        var registry = BuildRegistry();
        var args = ParseJson("{}");

        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                GitHubSkillRegistry.GetInstallationTokenTool,
                args,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = BuildRegistry();
        var args = ParseJson("{}");
        var context = AgentContext(AgentId);

        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("github.unknown_tool", args, context, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeAsync_AgentWithNoMemberships_FailsWithClearMessage()
    {
        var configStore = new InMemoryConfigStore();
        var memberships = new InMemoryUnitMembershipRepository();
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        var registry = BuildRegistry(configStore, memberships, subunits);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                GitHubSkillRegistry.GetInstallationTokenTool,
                ParseJson("{}"),
                AgentContext(AgentId),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("no parent unit");
    }

    [Fact]
    public async Task InvokeAsync_AgentBoundToUnitWithoutGitHubBinding_FailsWithBindingMissingMessage()
    {
        var configStore = new InMemoryConfigStore();
        var memberships = new InMemoryUnitMembershipRepository();
        memberships.Add(unitId: UnitId, agentId: AgentId);
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        var registry = BuildRegistry(configStore, memberships, subunits);

        var ex = await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                GitHubSkillRegistry.GetInstallationTokenTool,
                ParseJson("{}"),
                AgentContext(AgentId),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("no unit in its parent chain bound to the GitHub connector");
        ex.Message.ShouldContain("github.get_installation_token");
    }

    [Fact]
    public async Task InvokeAsync_AgentBoundToGitHubUnit_ReturnsTokenFromResolver()
    {
        var configStore = new InMemoryConfigStore();
        configStore.SetGithub(UnitId, new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var memberships = new InMemoryUnitMembershipRepository();
        memberships.Add(unitId: UnitId, agentId: AgentId);
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        var expiry = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var resolver = new StubBindingAuthResolver(new GitHubAuthCredential(
            "ghs_resolved_token_value", GitHubAuthCredentialKind.AppInstallation, expiry));
        var registry = BuildRegistry(configStore, memberships, subunits, resolver);

        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.GetInstallationTokenTool,
            ParseJson("{}"),
            AgentContext(AgentId),
            TestContext.Current.CancellationToken);

        result.GetProperty("token").GetString().ShouldBe("ghs_resolved_token_value");
        result.GetProperty("kind").GetString().ShouldBe("app_installation");
        result.GetProperty("expires_at").GetString().ShouldBe(expiry.ToString("o"));
        result.GetProperty("binding_owner_unit_id").GetString()
            .ShouldBe(GuidFormatter.Format(UnitId));
    }

    [Fact]
    public async Task InvokeAsync_PatBranchOmitsExpiry()
    {
        var configStore = new InMemoryConfigStore();
        configStore.SetGithub(
            UnitId,
            new UnitGitHubConfig("acme/platform", AppInstallationId: null, PatSecretName: "binding/abc/github/pat"));
        var memberships = new InMemoryUnitMembershipRepository();
        memberships.Add(unitId: UnitId, agentId: AgentId);
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        var resolver = new StubBindingAuthResolver(new GitHubAuthCredential(
            "ghp_pat_token", GitHubAuthCredentialKind.PersonalAccessToken, ExpiresAt: null));
        var registry = BuildRegistry(configStore, memberships, subunits, resolver);

        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.GetInstallationTokenTool,
            ParseJson("{}"),
            AgentContext(AgentId),
            TestContext.Current.CancellationToken);

        result.GetProperty("token").GetString().ShouldBe("ghp_pat_token");
        result.GetProperty("kind").GetString().ShouldBe("pat");
        result.GetProperty("expires_at").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task InvokeAsync_UnitSubject_FindsBindingDirectly()
    {
        // When the caller IS a unit (not an agent), the tool resolves the
        // binding from the unit itself rather than walking memberships.
        var configStore = new InMemoryConfigStore();
        configStore.SetGithub(UnitId, new UnitGitHubConfig("acme/platform", 4242));
        var memberships = new InMemoryUnitMembershipRepository();
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        var resolver = new StubBindingAuthResolver(new GitHubAuthCredential(
            "ghs_unit_caller", GitHubAuthCredentialKind.AppInstallation, DateTimeOffset.UtcNow.AddHours(1)));
        var registry = BuildRegistry(configStore, memberships, subunits, resolver);

        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.GetInstallationTokenTool,
            ParseJson("{}"),
            UnitContext(UnitId),
            TestContext.Current.CancellationToken);

        result.GetProperty("token").GetString().ShouldBe("ghs_unit_caller");
    }

    [Fact]
    public async Task InvokeAsync_AncestorUnitOwnsBinding_TokenStillResolved()
    {
        // Inheritance walk: agent → direct parent unit (no binding) →
        // ancestor unit (GitHub binding). The token-fetch tool must follow
        // the same ancestor walk the grant resolver uses when surfacing
        // the tool to the caller in the first place, otherwise the agent
        // would see the tool in its grants but get a "not bound" error
        // when calling it.
        var ancestor = new Guid("cccccccc-aaaa-bbbb-cccc-dddddddddddd");
        var configStore = new InMemoryConfigStore();
        configStore.SetGithub(ancestor, new UnitGitHubConfig("acme/platform", 4242));
        var memberships = new InMemoryUnitMembershipRepository();
        memberships.Add(unitId: UnitId, agentId: AgentId);
        var subunits = new InMemoryUnitSubunitMembershipRepository();
        subunits.AddEdge(parent: ancestor, child: UnitId);
        var resolver = new StubBindingAuthResolver(new GitHubAuthCredential(
            "ghs_ancestor_token", GitHubAuthCredentialKind.AppInstallation, DateTimeOffset.UtcNow.AddHours(1)));
        var registry = BuildRegistry(configStore, memberships, subunits, resolver);

        var result = await registry.InvokeAsync(
            GitHubSkillRegistry.GetInstallationTokenTool,
            ParseJson("{}"),
            AgentContext(AgentId),
            TestContext.Current.CancellationToken);

        result.GetProperty("token").GetString().ShouldBe("ghs_ancestor_token");
        result.GetProperty("binding_owner_unit_id").GetString()
            .ShouldBe(GuidFormatter.Format(ancestor));
    }

    // --- Helpers ---

    private static JsonElement ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static ToolCallContext AgentContext(Guid agentId) =>
        new(GuidFormatter.Format(agentId), Address.AgentScheme, ThreadId: GuidFormatter.Format(Guid.NewGuid()));

    private static ToolCallContext UnitContext(Guid unitId) =>
        new(GuidFormatter.Format(unitId), Address.UnitScheme, ThreadId: GuidFormatter.Format(Guid.NewGuid()));

    private static GitHubSkillRegistry BuildRegistry()
        => BuildRegistry(new InMemoryConfigStore(),
            new InMemoryUnitMembershipRepository(),
            new InMemoryUnitSubunitMembershipRepository());

    private static GitHubSkillRegistry BuildRegistry(
        IUnitConnectorConfigStore configStore,
        InMemoryUnitMembershipRepository memberships,
        InMemoryUnitSubunitMembershipRepository subunits,
        GitHubBindingAuthResolver? resolver = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configStore);
        services.AddSingleton<IUnitMembershipRepository>(memberships);
        services.AddSingleton<IUnitSubunitMembershipRepository>(subunits);
        var sp = services.BuildServiceProvider();

        return new GitHubSkillRegistry(
            sp.GetRequiredService<IServiceScopeFactory>(),
            resolver ?? new StubBindingAuthResolver(new GitHubAuthCredential(
                "ghs_default", GitHubAuthCredentialKind.AppInstallation, DateTimeOffset.UtcNow.AddHours(1))),
            NullLoggerFactory.Instance);
    }

    /// <summary>Test-only in-memory <see cref="IUnitConnectorConfigStore"/>.</summary>
    private sealed class InMemoryConfigStore : IUnitConnectorConfigStore
    {
        private readonly Dictionary<string, UnitConnectorBinding> _store =
            new(StringComparer.Ordinal);

        public void SetGithub(Guid unitId, UnitGitHubConfig config)
        {
            var element = JsonSerializer.SerializeToElement(
                config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            _store[GuidFormatter.Format(unitId)] =
                new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, element);
        }

        public Task<UnitConnectorBinding?> GetAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.TryGetValue(unitId, out var b) ? b : null);

        public Task SetAsync(string unitId, Guid typeId, JsonElement config, CancellationToken cancellationToken = default)
        {
            _store[unitId] = new UnitConnectorBinding(typeId, config);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
        {
            _store.Remove(unitId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryUnitMembershipRepository : IUnitMembershipRepository
    {
        private readonly List<UnitMembership> _rows = new();

        public void Add(Guid unitId, Guid agentId)
        {
            _rows.Add(new UnitMembership(unitId, agentId)
            {
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task UpsertAsync(UnitMembership membership, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteAllForAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<UnitMembership?> UpdateRolesAndExpertiseAsync(
            Guid unitId, Guid agentId,
            IReadOnlyList<string> roles, IReadOnlyList<string> expertise,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<UnitMembership>> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<UnitMembership>> ListByAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<UnitMembership> filtered = _rows
                .Where(r => r.AgentId == agentId)
                .ToList();
            return Task.FromResult(filtered);
        }
        public Task<IReadOnlyList<UnitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UnitMembership>>(_rows);
    }

    private sealed class InMemoryUnitSubunitMembershipRepository : IUnitSubunitMembershipRepository
    {
        private readonly List<(Guid Parent, Guid Child)> _edges = new();

        public void AddEdge(Guid parent, Guid child) => _edges.Add((parent, child));

        public Task UpsertAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<UnitSubunitMembership> UpsertAsync(
            Guid parentId, Guid childId,
            IReadOnlyList<string> roles, IReadOnlyList<string> expertise,
            CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<UnitSubunitMembership?> GetAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteAsync(Guid parentId, Guid childId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task DeleteAllForUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<UnitSubunitMembership>> ListByParentAsync(Guid parentId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<UnitSubunitMembership>> ListByChildAsync(Guid childId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<UnitSubunitMembership> filtered = _edges
                .Where(e => e.Child == childId)
                .Select(e => new UnitSubunitMembership(e.Parent, e.Child)
                {
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                })
                .ToList();
            return Task.FromResult(filtered);
        }
        public Task<IReadOnlyList<UnitSubunitMembership>> ListAllAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Stub <see cref="GitHubBindingAuthResolver"/> that returns a canned
    /// credential without touching the App-JWT mint or the secret store.
    /// Mirrors the pattern <see cref="GitHubConnectorRuntimeContextContributorTests"/>
    /// uses for its tests.
    /// </summary>
    private sealed class StubBindingAuthResolver : GitHubBindingAuthResolver
    {
        private readonly GitHubAuthCredential _credential;

        public StubBindingAuthResolver(GitHubAuthCredential credential)
            : base(
                new StubAppAuth(),
                NSubstitute.Substitute.For<IInstallationTokenCache>(),
                NoOpScopeFactory.Instance,
                NullLogger<GitHubBindingAuthResolver>.Instance)
        {
            _credential = credential;
        }

        public override Task<GitHubAuthCredential> ResolveAsync(
            UnitGitHubConfig binding,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_credential);
    }

    private sealed class StubAppAuth : GitHubAppAuth
    {
        public StubAppAuth() : base(BuildOptions(), NullLoggerFactory.Instance) { }

        private static GitHubConnectorOptions BuildOptions()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            return new GitHubConnectorOptions
            {
                AppId = 1,
                PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(),
            };
        }
    }

    private sealed class NoOpScopeFactory : IServiceScopeFactory
    {
        public static readonly NoOpScopeFactory Instance = new();
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("StubBindingAuthResolver bypasses the scope factory.");
    }
}
