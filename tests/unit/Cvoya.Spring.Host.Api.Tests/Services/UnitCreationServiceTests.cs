// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Security.Claims;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Service-level tests for <see cref="UnitCreationService"/>. Exercises the
/// creator-identity resolution path introduced for #324 without needing the
/// full HTTP pipeline.
/// </summary>
public class UnitCreationServiceTests
{
    private static readonly Guid Agent_TechLead_Id = new("00000001-feed-1234-5678-000000000000");

    // Stable UUIDs returned by the mock IHumanIdentityResolver.
    private static readonly Guid FallbackGuid = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid AliceGuid = new("aaaaaaaa-0000-0000-0000-000000000001");

    // #1666: helper for Arg.Is<> assertions that need to verify a string
    // is the actor Guid in GuidFormatter "N" form. Wrapped in a method
    // because NSubstitute's Arg.Is<> takes an expression tree that can't
    // hold an `out` declaration directly.
    private static bool IsGuidN(string id) => Guid.TryParseExact(id, "N", out _);

    /// <summary>
    /// ADR-0043 §5g: <see cref="MemberManifest"/>'s <c>Agent</c> / <c>Unit</c>
    /// slots are <see cref="InlineArtefactDefinition"/> — a union of bare
    /// scalar reference and inline body. These helpers wrap a bare reference
    /// the way the prior <c>{ Agent = "name" }</c> initializer used to.
    /// </summary>
    private static class Member
    {
        public static MemberManifest AgentRef(string name) =>
            new() { Agent = InlineArtefactDefinition.FromReference(name) };

        public static MemberManifest UnitRef(string name) =>
            new() { Unit = InlineArtefactDefinition.FromReference(name) };
    }

    [Fact]
    public async Task CreateAsync_NoHttpContext_FallsBackToApiIdentity()
    {
        // When the service runs outside a request — no ambient HttpContext —
        // the creator identity falls back to the synthetic "api" user so the
        // Owner grant still lands on a deterministic, well-known id rather
        // than an empty string or null. Matches the existing fallback used
        // by MessageEndpoints / AgentEndpoints.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-ctx-unit");

        // Post-#1629 Unit.Name carries the address Path (Guid hex), not the
        // request slug — DisplayName carries the human-readable label.
        result.Unit.DisplayName.ShouldBe("no-ctx-unit");

        // The grant went to the UUID that the resolver returned for the fallback username.
        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            FallbackGuid,
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == FallbackGuid.ToString()
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AuthenticatedUser_GrantsOwnerToNameIdentifierClaim()
    {
        // When the request arrives with an authenticated principal, the
        // grant must land on that principal's resolved UUID — the same id
        // PermissionHandler consults when evaluating subsequent permission
        // checks after the #1491 migration.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice@example.com") },
            authenticationType: "test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("auth-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            AliceGuid,
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == AliceGuid.ToString()
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
        await fixture.Proxy.DidNotReceive().SetHumanPermissionAsync(
            FallbackGuid,
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnauthenticatedPrincipal_FallsBackToApi()
    {
        // An HttpContext with an anonymous ClaimsPrincipal (Identity.IsAuthenticated == false)
        // must NOT be treated as a real caller — fall back to "api" the same
        // way we do when no HttpContext is present at all.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("anon-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            FallbackGuid,
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Minimal fixture wiring the <see cref="UnitCreationService"/> up with
    /// stubs for every collaborator so a single test can focus on one
    /// behaviour at a time. <see cref="HttpContextAccessor"/> is exposed so
    /// each test arranges the ambient context as needed.
    /// </summary>
    private sealed class Fixture
    {
        public IDirectoryService Directory { get; } = Substitute.For<IDirectoryService>();
        public IActorProxyFactory ActorProxyFactory { get; } = Substitute.For<IActorProxyFactory>();
        public IHttpContextAccessor HttpContextAccessor { get; } = Substitute.For<IHttpContextAccessor>();
        public IUnitConnectorConfigStore ConnectorConfigStore { get; } = Substitute.For<IUnitConnectorConfigStore>();
        public ISkillBundleResolver BundleResolver { get; } = Substitute.For<ISkillBundleResolver>();
        public ISkillBundleValidator BundleValidator { get; } = Substitute.For<ISkillBundleValidator>();
        public IUnitSkillBundleStore BundleStore { get; } = Substitute.For<IUnitSkillBundleStore>();
        public IUnitMemberGraphStore MemberGraphStore { get; } = Substitute.For<IUnitMemberGraphStore>();
        public ITenantContext TenantContext { get; } = Substitute.For<ITenantContext>();
        public IUnitActor Proxy { get; } = Substitute.For<IUnitActor>();
        public Cvoya.Spring.Core.Execution.ILlmCredentialResolver CredentialResolver { get; } =
            Substitute.For<Cvoya.Spring.Core.Execution.ILlmCredentialResolver>();
        public Cvoya.Spring.Core.Execution.IUnitExecutionStore ExecutionStore { get; } =
            Substitute.For<Cvoya.Spring.Core.Execution.IUnitExecutionStore>();
        public Cvoya.Spring.Core.Catalog.IRuntimeCatalog? RuntimeCatalog { get; }
        public UnitCreationService Service { get; }

        /// <param name="fallbackGuid">UUID returned when the resolver is called with the fallback username ("api").</param>
        /// <param name="aliceGuid">UUID returned when the resolver is called with any other username.</param>
        /// <param name="runtimeCatalog">
        /// Optional runtime catalogue. When supplied, the auto-start gate
        /// (#2204) wires up; without it the gate short-circuits to Draft
        /// for the same reason production wiring without a catalogue does.
        /// </param>
        public Fixture(
            Guid fallbackGuid,
            Guid aliceGuid,
            Cvoya.Spring.Core.Catalog.IRuntimeCatalog? runtimeCatalog = null)
        {
            RuntimeCatalog = runtimeCatalog;
            Directory
                .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(LifecycleStatus.Draft);
            ActorProxyFactory
                .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Proxy);
            // IHumanActor resolution is used for the mirror grant; returning
            // null would throw NRE inside the service's try/catch — tests
            // treat that as a non-fatal path and don't care about the mirror
            // itself, but wiring a stub keeps logs clean.
            ActorProxyFactory
                .CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Substitute.For<IHumanActor>());

            // Wire a real ServiceCollection so the scope factory returns a
            // mock IHumanIdentityResolver without needing a real DbContext.
            var identityResolver = Substitute.For<IHumanIdentityResolver>();
            identityResolver
                .ResolveByUsernameAsync(UnitCreationService.FallbackCreatorId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(fallbackGuid);
            identityResolver
                .ResolveByUsernameAsync(Arg.Is<string>(s => s != UnitCreationService.FallbackCreatorId), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(aliceGuid);

            var services = new ServiceCollection();
            services.AddScoped<IHumanIdentityResolver>(_ => identityResolver);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            TenantContext.CurrentTenantId.Returns(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

            Service = new UnitCreationService(
                Directory,
                ActorProxyFactory,
                HttpContextAccessor,
                ConnectorConfigStore,
                Array.Empty<IConnectorType>(),
                BundleResolver,
                BundleValidator,
                BundleStore,
                MemberGraphStore,
                TenantContext,
                scopeFactory,
                NullLoggerFactory.Instance,
                executionStore: ExecutionStore,
                credentialResolver: CredentialResolver,
                runtimeCatalog: RuntimeCatalog);
        }

        public Task<UnitCreationResult> CreateAsync(string name)
            => Service.CreateAsync(
                new CreateUnitRequest(
                    Name: name,
                    DisplayName: name,
                    Description: "test",
                    Model: null,
                    Color: null,
                    Connector: null,
                    // Review feedback on #744: every unit needs a parent.
                    // Legacy tests exercise the "create from scratch"
                    // shape, which maps to top-level.
                    IsTopLevel: true),
                CancellationToken.None);

        public Task<UnitCreationResult> CreateFromManifestAsync(
            string name,
            IEnumerable<MemberManifest> members)
            => Service.CreateFromManifestAsync(
                new UnitManifest
                {
                    Name = name,
                    Description = $"{name} description",
                    Members = members.ToList(),
                },
                new UnitCreationOverrides(IsTopLevel: true),
                CancellationToken.None);
    }

    // --- #340 / #2072: template creation writes agent memberships through the canonical actor path ---

    [Fact]
    public async Task CreateFromManifestAsync_AgentMembers_AddsMembershipsThroughActorProxy()
    {
        // Regression test for #340 (membership rows must reach EF for
        // template-created units) and #2072 (the canonical write path is
        // UnitActor.AddMemberAsync, which routes through
        // UnitMembershipCoordinator → IUnitMemberGraphStore). The previous
        // direct IUnitMembershipRepository.UpsertAsync call here was a
        // redundant second write to the same EF row.
        //
        // After #1629 the service resolves manifest slugs to stable UUIDs
        // by walking the directory's ListAllAsync result and matching on
        // DisplayName == slug. The test seeds those entries so the lookup
        // returns deterministic UUIDs.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var techLeadUuid = new Guid("aadaadaa-0000-0000-0000-000000000001");
        var backendUuid = new Guid("aadaadaa-0000-0000-0000-000000000002");
        var qaUuid = new Guid("aadaadaa-0000-0000-0000-000000000003");

        var seededEntries = new List<DirectoryEntry>
        {
            new(new Address("agent", techLeadUuid), techLeadUuid, "tech-lead", string.Empty, null, DateTimeOffset.UtcNow),
            new(new Address("agent", backendUuid), backendUuid, "backend-engineer", string.Empty, null, DateTimeOffset.UtcNow),
            new(new Address("agent", qaUuid), qaUuid, "qa-engineer", string.Empty, null, DateTimeOffset.UtcNow),
        };
        fixture.Directory
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(seededEntries);

        // The auto-register path then calls Resolve(agent, <guid-hex>) — return
        // the same entry so it is treated as already-registered.
        foreach (var entry in seededEntries)
        {
            fixture.Directory
                .ResolveAsync(
                    Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == entry.ActorId),
                    Arg.Any<CancellationToken>())
                .Returns(entry);
        }

        var members = new[]
        {
            Member.AgentRef("tech-lead"),
            Member.AgentRef("backend-engineer"),
            Member.AgentRef("qa-engineer"),
        };

        var result = await fixture.CreateFromManifestAsync("eng-team", members);

        result.MembersAdded.ShouldBe(3);

        // Each agent member reaches EF via the actor surface — the proxy
        // call is the only write the service issues.
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == techLeadUuid),
            Arg.Any<CancellationToken>());
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == backendUuid),
            Arg.Any<CancellationToken>());
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == qaUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_UnitTypedMember_AddsMembershipThroughActorProxy()
    {
        // Unit-typed members were never written to unit_memberships (that
        // table is agent-addressed; #2052 / ADR-0040 keeps unit-typed edges
        // in unit_subunit_memberships under the actor's coordinator). The
        // template path forwards the add through the actor proxy regardless
        // of scheme, so the assertion is symmetric with the agent case.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var members = new[]
        {
            Member.UnitRef("sub-team"),
        };

        var result = await fixture.CreateFromManifestAsync("parent-unit", members);

        result.MembersAdded.ShouldBe(1);

        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit"),
            Arg.Any<CancellationToken>());
    }

    // --- #374: template creation auto-registers agent directory entries ---

    [Fact]
    public async Task CreateFromManifestAsync_AgentMembers_RegistersAgentDirectoryEntries()
    {
        // Regression test for #374. Template-created agents should be
        // auto-registered in the directory so GET /api/v1/agents returns them
        // and the dashboard's Agents section populates.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var members = new[]
        {
            Member.AgentRef("tech-lead"),
            Member.AgentRef("backend-engineer"),
            Member.AgentRef("qa-engineer"),
        };

        var result = await fixture.CreateFromManifestAsync("eng-team", members);

        result.MembersAdded.ShouldBe(3);

        // Each agent member should be auto-registered: post-#1629 the
        // service mints a fresh Guid for each manifest slug and registers
        // an agent-scheme entry whose DisplayName carries the slug. Three
        // distinct agent registrations land per call.
        await fixture.Directory.Received(3).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "agent"),
            Arg.Any<CancellationToken>());
        foreach (var m in members)
        {
            await fixture.Directory.Received().RegisterAsync(
                Arg.Is<DirectoryEntry>(e =>
                    e.Address.Scheme == "agent"
                    && e.DisplayName == m.AgentName
                    && e.Description == string.Empty),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task CreateFromManifestAsync_AgentAlreadyRegistered_DoesNotDuplicate()
    {
        // Idempotency: if the agent already exists in the directory (e.g.
        // created via `spring agent create` before being added to the unit),
        // the existing entry is preserved — no duplicate, no error.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        // Seed the directory so manifest slug "tech-lead" resolves to a
        // stable Guid and looks already-registered to the auto-register
        // step — RegisterAsync should not be called for it.
        var existingEntry = new DirectoryEntry(
            new Address("agent", Agent_TechLead_Id),
            Agent_TechLead_Id,
            "tech-lead",
            "already exists",
            null,
            DateTimeOffset.UtcNow);
        fixture.Directory
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry> { existingEntry });
        fixture.Directory
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == Agent_TechLead_Id),
                Arg.Any<CancellationToken>())
            .Returns(existingEntry);

        var members = new[]
        {
            Member.AgentRef("tech-lead"),
            Member.AgentRef("backend-engineer"),
        };

        var result = await fixture.CreateFromManifestAsync("eng-team-idem", members);

        result.MembersAdded.ShouldBe(2);

        // tech-lead resolved as non-null so the auto-register skips it.
        // backend-engineer resolved as null (default) so it gets registered:
        // exactly one agent-scheme RegisterAsync call should occur.
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Scheme == "agent"),
            Arg.Any<CancellationToken>());
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent"
                && e.DisplayName == "backend-engineer"),
            Arg.Any<CancellationToken>());
    }

    // --- T-05 (#947): differentiated creation — Draft vs Validating ---

    [Fact]
    public async Task CreateAsync_DirectCreate_StaysInDraftPendingExecutionConfig()
    {
        // ADR-0038: the CreateUnitRequest body no longer carries flat
        // `provider`. The IsFullyConfiguredForValidationAsync gate
        // requires both model and provider, so a direct-create stays in
        // Draft until the operator pushes a structured execution block
        // through `PUT /units/{id}/execution`. PR-2 will revisit the
        // direct-create flow to take the structured shape.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "model-only-unit",
                DisplayName: "model-only-unit",
                Description: "test",
                Model: "claude-sonnet-4-6",
                Color: null,
                Connector: null,
                IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(LifecycleStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            LifecycleStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_PartialConfig_MissingCredential_StaysDraft()
    {
        // Model + provider supplied but no credential resolvable: the unit
        // cannot be validated end-to-end yet, so it stays in Draft. The
        // user finishes configuration and later calls /revalidate.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.CredentialResolver
            .ResolveAsync(
                Arg.Any<string>(), Arg.Any<Cvoya.Spring.Core.Catalog.AuthMethod>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.LlmCredentialResolution(
                Value: null,
                Source: Cvoya.Spring.Core.Execution.LlmCredentialSource.NotFound,
                SecretName: "anthropic-api-key"));

        var result = await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "missing-cred-unit",
                DisplayName: "missing-cred-unit",
                Description: "test",
                Model: "claude-sonnet-4-6",
                Color: null,
                Connector: null,

                IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(LifecycleStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            LifecycleStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithoutModel_StatusIsDraft()
    {
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-model-unit");

        result.Unit.Status.ShouldBe(LifecycleStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            LifecycleStatus.Validating, Arg.Any<CancellationToken>());
    }

    // --- #2204: regression — package-installed units must auto-start ---

    [Fact]
    public async Task CreateFromManifestAsync_FullConfig_AutoStartsValidation()
    {
        // Regression for #2204: PR #2162 added SetPendingAutoStartAsync but
        // the gate guarding it never passed for manifest-installed units —
        // `provider` was never threaded through from the manifest, so
        // IsFullyConfiguredForValidationAsync returned false and the unit
        // stayed in Draft. The refactor reads execution defaults from the
        // store and resolves provider via the runtime catalogue (same chain
        // the validation scheduler uses), so a manifest declaring
        // ai.runtime + ai.model + execution.image now reaches Validating
        // and arms the post-validation auto-start flag.
        var runtimeCatalog = Substitute.For<Cvoya.Spring.Core.Catalog.IRuntimeCatalog>();
        runtimeCatalog.GetAgentRuntime("claude-code").Returns(
            new Cvoya.Spring.Core.Catalog.AgentRuntime(
                Id: "claude-code",
                DisplayName: "Claude Code",
                DefaultImage: "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Launcher: "claude-code-cli",
                ThreadBinding: new Cvoya.Spring.Core.Catalog.ThreadBinding(
                    Cvoya.Spring.Core.Catalog.ThreadBindingKind.CliArg, "--resume", null),
                SystemPromptInjection: new Cvoya.Spring.Core.Catalog.SystemPromptInjection(
                    Cvoya.Spring.Core.Catalog.SystemPromptInjectionKind.File, "AGENTS.md", null),
                ModelProviders: new[]
                {
                    new Cvoya.Spring.Core.Catalog.AgentRuntimeProviderEdge(
                        Id: "anthropic",
                        AuthMethod: Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
                        CredentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN"),
                }));

        var fixture = new Fixture(FallbackGuid, AliceGuid, runtimeCatalog);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        // Simulate the execution row that PersistUnitExecutionAsync wrote
        // (manifest path), so the gate reads back a fully configured unit.
        fixture.ExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.UnitExecutionDefaults(
                Image: "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                Agent: "claude-code"));

        fixture.CredentialResolver
            .ResolveAsync(
                "anthropic",
                Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
                Arg.Any<Guid?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.LlmCredentialResolution(
                Value: "oauth-token",
                Source: Cvoya.Spring.Core.Execution.LlmCredentialSource.Tenant,
                SecretName: "anthropic-claude-code-oauth-token"));

        // Actor accepts the Validating transition — that's the precondition
        // for arming the PendingAutoStart flag the actor consumes after
        // CompleteValidationAsync to drive Stopped → Starting → Running.
        fixture.Proxy
            .TransitionAsync(LifecycleStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, LifecycleStatus.Validating, null));

        var manifest = new UnitManifest
        {
            Name = "auto-start-unit",
            Description = "manifest with full ai+execution config",
            Ai = new AiManifest
            {
                Runtime = "claude-code",
                Model = new AiModelManifest { Provider = "anthropic", Id = "claude-opus-4-7" },
            },
            Execution = new ExecutionManifest
            {
                Image = "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Model = "claude-opus-4-7",
            },
        };

        var result = await fixture.Service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(LifecycleStatus.Validating);
        await fixture.Proxy.Received(1).TransitionAsync(
            LifecycleStatus.Validating, Arg.Any<CancellationToken>());
        await fixture.Proxy.Received(1).SetPendingAutoStartAsync(
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_MissingCredential_StaysInDraft()
    {
        // Mirror of the positive test: same manifest and runtime catalogue,
        // but the credential resolver reports NotFound. The gate must close
        // and the unit must stay in Draft — no transition, no pending flag.
        var runtimeCatalog = Substitute.For<Cvoya.Spring.Core.Catalog.IRuntimeCatalog>();
        runtimeCatalog.GetAgentRuntime("claude-code").Returns(
            new Cvoya.Spring.Core.Catalog.AgentRuntime(
                Id: "claude-code",
                DisplayName: "Claude Code",
                DefaultImage: "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Launcher: "claude-code-cli",
                ThreadBinding: new Cvoya.Spring.Core.Catalog.ThreadBinding(
                    Cvoya.Spring.Core.Catalog.ThreadBindingKind.CliArg, "--resume", null),
                SystemPromptInjection: new Cvoya.Spring.Core.Catalog.SystemPromptInjection(
                    Cvoya.Spring.Core.Catalog.SystemPromptInjectionKind.File, "AGENTS.md", null),
                ModelProviders: new[]
                {
                    new Cvoya.Spring.Core.Catalog.AgentRuntimeProviderEdge(
                        Id: "anthropic",
                        AuthMethod: Cvoya.Spring.Core.Catalog.AuthMethod.Oauth,
                        CredentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN"),
                }));

        var fixture = new Fixture(FallbackGuid, AliceGuid, runtimeCatalog);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.ExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.UnitExecutionDefaults(
                Image: "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Provider: "anthropic",
                Model: "claude-opus-4-7",
                Agent: "claude-code"));
        fixture.CredentialResolver
            .ResolveAsync(
                Arg.Any<string>(),
                Arg.Any<Cvoya.Spring.Core.Catalog.AuthMethod>(),
                Arg.Any<Guid?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.LlmCredentialResolution(
                Value: null,
                Source: Cvoya.Spring.Core.Execution.LlmCredentialSource.NotFound,
                SecretName: "anthropic-claude-code-oauth-token"));

        var manifest = new UnitManifest
        {
            Name = "no-cred-unit",
            Description = "full config but no credential resolved",
            Ai = new AiManifest
            {
                Runtime = "claude-code",
                Model = new AiModelManifest { Provider = "anthropic", Id = "claude-opus-4-7" },
            },
            Execution = new ExecutionManifest
            {
                Image = "ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest",
                Model = "claude-opus-4-7",
            },
        };

        var result = await fixture.Service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(LifecycleStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            LifecycleStatus.Validating, Arg.Any<CancellationToken>());
        await fixture.Proxy.DidNotReceive().SetPendingAutoStartAsync(
            Arg.Any<CancellationToken>());
    }

    // --- #1065: provider must NOT leak into execution defaults as a runtime id ---

    [Fact]
    public async Task CreateAsync_WithModelOnly_PersistsModelInExecutionDefaults()
    {
        // ADR-0038: the CreateUnitRequest body no longer carries flat
        // `provider`; the direct-create execution-defaults mirror writes
        // only the model id. Provider lives on the structured
        // `execution.model.provider` slot now and is set via the
        // dedicated execution-set endpoint.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "ollama-no-runtime",
                DisplayName: "ollama-no-runtime",
                Description: "test",
                Model: "llama3.2:3b",
                Color: null,
                Connector: null,
                IsTopLevel: true),
            CancellationToken.None);

        await fixture.ExecutionStore.Received(1).SetAsync(
            Arg.Is<string>(id => IsGuidN(id)),
            Arg.Is<Cvoya.Spring.Core.Execution.UnitExecutionDefaults>(d =>
                d.Provider == null
                && d.Model == "llama3.2:3b"),
            Arg.Any<CancellationToken>());
    }

    // #1732: the symmetric "tool-only create leaves Provider null"
    // assertion is obsolete — Tool is no longer threaded through the
    // CreateUnitRequest body.

    // --- #1065 side-note: unit-detail GET surfaces Provider/Hosting ---
    // The actor-side round-trip is verified in UnitActorTests; this test
    // pins the wire-shape contract that Provider/Hosting flow into
    // SetMetadataAsync from the create path so the unit-detail GET has
    // values to project.
    // #1732: Tool was dropped from the unit-actor metadata — derived from
    // execution.agent at dispatch.

    [Fact]
    public async Task CreateAsync_WithHosting_FlowsThroughSetMetadata()
    {
        // ADR-0038: provider was dropped from the unit-create wire shape;
        // hosting still flows through SetMetadataAsync.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "metadata-roundtrip",
                DisplayName: "metadata-roundtrip",
                Description: "test",
                Model: "llama3.2:3b",
                Color: null,
                Connector: null,
                Hosting: "ephemeral",
                IsTopLevel: true),
            CancellationToken.None);

        await fixture.Proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m =>
                m.Hosting == "ephemeral"
                && m.Model == "llama3.2:3b"),
            Arg.Any<CancellationToken>());
    }
}
