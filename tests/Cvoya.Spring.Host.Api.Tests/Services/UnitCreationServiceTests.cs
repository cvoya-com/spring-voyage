// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Security.Claims;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
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
        public IUnitMembershipRepository MembershipRepository { get; } = Substitute.For<IUnitMembershipRepository>();
        public IUnitActor Proxy { get; } = Substitute.For<IUnitActor>();
        public Cvoya.Spring.Core.Execution.ILlmCredentialResolver CredentialResolver { get; } =
            Substitute.For<Cvoya.Spring.Core.Execution.ILlmCredentialResolver>();
        public Cvoya.Spring.Core.Execution.IUnitExecutionStore ExecutionStore { get; } =
            Substitute.For<Cvoya.Spring.Core.Execution.IUnitExecutionStore>();
        public UnitCreationService Service { get; }

        /// <param name="fallbackGuid">UUID returned when the resolver is called with the fallback username ("api").</param>
        /// <param name="aliceGuid">UUID returned when the resolver is called with any other username.</param>
        public Fixture(Guid fallbackGuid, Guid aliceGuid)
        {
            Directory
                .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
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

            Service = new UnitCreationService(
                Directory,
                ActorProxyFactory,
                HttpContextAccessor,
                ConnectorConfigStore,
                Array.Empty<IConnectorType>(),
                BundleResolver,
                BundleValidator,
                BundleStore,
                MembershipRepository,
                scopeFactory,
                NullLoggerFactory.Instance,
                executionStore: ExecutionStore,
                credentialResolver: CredentialResolver);
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

    // --- #340: template creation writes agent memberships through to the DB ---

    [Fact]
    public async Task CreateFromManifestAsync_AgentMembers_WritesMembershipRow()
    {
        // Regression test for #340. Actor-state add via proxy.AddMemberAsync
        // was already happening; this verifies the parallel DB write-through
        // now lands on the membership repository for every agent member.
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
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
            new MemberManifest { Agent = "qa-engineer" },
        };

        var result = await fixture.CreateFromManifestAsync("eng-team", members);

        result.MembersAdded.ShouldBe(3);

        // The UnitId is the unit's actor Guid, minted internally by the
        // service. Assert on the agent ids only — those are deterministic.
        await fixture.MembershipRepository.Received(1).UpsertAsync(
            Arg.Is<UnitMembership>(u =>
                u.AgentId == techLeadUuid
                && u.Enabled && u.Model == null && u.Specialty == null && u.ExecutionMode == null),
            Arg.Any<CancellationToken>());

        await fixture.MembershipRepository.Received(1).UpsertAsync(
            Arg.Is<UnitMembership>(u =>
                u.AgentId == backendUuid
                && u.Enabled && u.Model == null && u.Specialty == null && u.ExecutionMode == null),
            Arg.Any<CancellationToken>());

        await fixture.MembershipRepository.Received(1).UpsertAsync(
            Arg.Is<UnitMembership>(u =>
                u.AgentId == qaUuid
                && u.Enabled && u.Model == null && u.Specialty == null && u.ExecutionMode == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_UnitTypedMember_DoesNotWriteMembershipRow()
    {
        // Per #217 scope: unit-typed members stay in actor state only — the
        // membership table is agent-addressed and polymorphic rows are a
        // future issue. The template fix must not leak unit rows into the
        // table through the same code path.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var members = new[]
        {
            new MemberManifest { Unit = "sub-team" },
        };

        var result = await fixture.CreateFromManifestAsync("parent-unit", members);

        result.MembersAdded.ShouldBe(1);
        await fixture.MembershipRepository.DidNotReceive().UpsertAsync(
            Arg.Any<UnitMembership>(),
            Arg.Any<CancellationToken>());

        // The actor-state add still happened — unit-typed membership is the
        // fast-path read until #217 lands polymorphic rows.
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_MembershipRepositoryThrows_ActorStateUpdatedWithWarning()
    {
        // Preferred failure mode per the fix plan: if the DB write fails
        // after the actor-state write, we log + surface a warning rather
        // than trying to roll back the actor state. Actor state is the
        // authoritative fast-path; a reconciler repairs divergence.
        var flakyUnitUuid = new Guid("f1a4f1a4-0000-0000-0000-000000000001");
        var lonelyAgentUuid = new Guid("10e1a6e1-0000-0000-0000-000000000001");

        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        // Post-#1629 the manifest-driven member resolution walks ListAllAsync
        // and matches DisplayName == slug. Seed the directory so the agent
        // resolves to a deterministic UUID before the throw path is exercised.
        fixture.Directory
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", lonelyAgentUuid), lonelyAgentUuid,
                    "lonely-agent", string.Empty, null, DateTimeOffset.UtcNow),
            });
        fixture.Directory
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == lonelyAgentUuid),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(new Address("agent", lonelyAgentUuid), lonelyAgentUuid,
                "lonely-agent", string.Empty, null, DateTimeOffset.UtcNow));

        fixture.MembershipRepository
            .UpsertAsync(Arg.Any<UnitMembership>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("db down")));

        var result = await fixture.CreateFromManifestAsync(
            "flaky-unit",
            new[] { new MemberManifest { Agent = "lonely-agent" } });

        // The actor-state add succeeded — tally reflects it.
        result.MembersAdded.ShouldBe(1);
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Id == lonelyAgentUuid),
            Arg.Any<CancellationToken>());

        // The DB-write failure surfaces as a warning on the creation result.
        result.Warnings.ShouldContain(w =>
            w.Contains("lonely-agent", StringComparison.Ordinal)
            && w.Contains("db down", StringComparison.Ordinal));
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
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
            new MemberManifest { Agent = "qa-engineer" },
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
                    && e.DisplayName == m.Agent
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
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
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
    public async Task CreateAsync_FullConfig_TransitionsToValidating()
    {
        // Full execution config (model + provider) + a resolvable credential
        // must send the unit straight into Validating so the Dapr
        // UnitValidationWorkflow kicks off the in-container probe.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.CredentialResolver
            .ResolveAsync(
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Execution.LlmCredentialResolution(
                Value: "sk-live",
                Source: Cvoya.Spring.Core.Execution.LlmCredentialSource.Tenant,
                SecretName: "anthropic-api-key"));
        fixture.Proxy.TransitionAsync(UnitStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Validating, null));

        var result = await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "full-config-unit",
                DisplayName: "full-config-unit",
                Description: "test",
                Model: "claude-sonnet-4-6",
                Color: null,
                Connector: null,
                Tool: "claude-code-cli",
                Provider: "claude",
                IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(UnitStatus.Validating);
        await fixture.Proxy.Received(1).TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());
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
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
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
                Provider: "claude",
                IsTopLevel: true),
            CancellationToken.None);

        result.Unit.Status.ShouldBe(UnitStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithoutModel_StatusIsDraft()
    {
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-model-unit");

        result.Unit.Status.ShouldBe(UnitStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());
    }

    // --- #1065: provider must NOT leak into the execution-defaults Runtime slot ---

    [Fact]
    public async Task CreateAsync_WithProviderOnly_DoesNotMirrorProviderIntoRuntime()
    {
        // Regression for #1065. The CreateUnitRequest body carries no
        // `runtime` field — that lives on the dedicated execution-set
        // surface (`unit execution set --runtime <docker|podman>`).
        // Pre-fix, the direct-create execution-defaults mirror wrote
        // `Runtime: provider`, so a unit created with `--provider ollama`
        // surfaced as `runtime: ollama` on `GET /api/v1/units/{id}/execution`
        // — a category error (LLM provider in the container-runtime slot).
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
                Tool: "dapr-agent",
                Provider: "ollama",
                IsTopLevel: true),
            CancellationToken.None);

        // The execution defaults must persist Tool/Provider/Model from the
        // request, but Runtime must stay null — the request has no runtime
        // field, and provider must not be mirrored into the runtime slot.
        // #1666: the unit id passed to the store is the actor's Guid in
        // GuidFormatter.Format ("N") form — DbUnitExecutionStore parses it
        // as a Guid, so the user-facing name would be silently rejected.
        await fixture.ExecutionStore.Received(1).SetAsync(
            Arg.Is<string>(id => IsGuidN(id)),
            Arg.Is<Cvoya.Spring.Core.Execution.UnitExecutionDefaults>(d =>
                d.Runtime == null
                && d.Provider == "ollama"
                && d.Tool == "dapr-agent"
                && d.Model == "llama3.2:3b"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithToolOnly_LeavesProviderAndRuntimeNull()
    {
        // Symmetric assertion: a tool-only create (e.g. `claude-code`,
        // no provider, no model) must leave both Provider and Runtime
        // null on the persisted execution defaults. Before #1065 the
        // mirror would have written Provider into Runtime, but a tool-
        // only create has no provider to mirror — so the bug was masked
        // for `--tool claude-code` alone. Lock the contract anyway so a
        // future regression that tries to default Runtime from anything
        // else (Tool, Hosting…) trips this test.
        var fixture = new Fixture(FallbackGuid, AliceGuid);
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        await fixture.Service.CreateAsync(
            new CreateUnitRequest(
                Name: "claude-only",
                DisplayName: "claude-only",
                Description: "test",
                Model: null,
                Color: null,
                Connector: null,
                Tool: "claude-code",
                Provider: null,
                IsTopLevel: true),
            CancellationToken.None);

        // #1666: the store id is the actor Guid (parsed by
        // DbUnitExecutionStore.SetAsync via GuidFormatter.TryParse), not
        // the user-facing name.
        await fixture.ExecutionStore.Received(1).SetAsync(
            Arg.Is<string>(id => IsGuidN(id)),
            Arg.Is<Cvoya.Spring.Core.Execution.UnitExecutionDefaults>(d =>
                d.Runtime == null
                && d.Provider == null
                && d.Tool == "claude-code"
                && d.Model == null),
            Arg.Any<CancellationToken>());
    }

    // --- #1065 side-note: unit-detail GET surfaces Tool/Provider/Hosting ---
    // The actor-side round-trip is verified in UnitActorTests; this test
    // pins the wire-shape contract that Tool/Provider/Hosting flow into
    // SetMetadataAsync from the create path so the unit-detail GET has
    // values to project.

    [Fact]
    public async Task CreateAsync_WithToolProviderHosting_FlowsThroughSetMetadata()
    {
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
                Tool: "dapr-agent",
                Provider: "ollama",
                Hosting: "ephemeral",
                IsTopLevel: true),
            CancellationToken.None);

        await fixture.Proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m =>
                m.Tool == "dapr-agent"
                && m.Provider == "ollama"
                && m.Hosting == "ephemeral"
                && m.Model == "llama3.2:3b"),
            Arg.Any<CancellationToken>());
    }
}