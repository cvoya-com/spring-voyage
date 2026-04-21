// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Security.Claims;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
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
    [Fact]
    public async Task CreateAsync_NoHttpContext_FallsBackToApiIdentity()
    {
        // When the service runs outside a request — no ambient HttpContext —
        // the creator identity falls back to the synthetic "api" user so the
        // Owner grant still lands on a deterministic, well-known id rather
        // than an empty string or null. Matches the existing fallback used
        // by MessageEndpoints / AgentEndpoints.
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-ctx-unit");

        result.Unit.Name.ShouldBe("no-ctx-unit");

        // The grant went to the fallback id — NOT to any claim.
        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == UnitCreationService.FallbackCreatorId
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AuthenticatedUser_GrantsOwnerToNameIdentifierClaim()
    {
        // When the request arrives with an authenticated principal, the
        // grant must land on that principal's NameIdentifier claim — the
        // same id PermissionHandler consults when evaluating subsequent
        // permission checks. This keeps the round-trip consistent: create
        // → Owner grant → subsequent call passes the router's Viewer gate.
        var fixture = new Fixture();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice@example.com") },
            authenticationType: "test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("auth-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            "alice@example.com",
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == "alice@example.com"
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
        await fixture.Proxy.DidNotReceive().SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnauthenticatedPrincipal_FallsBackToApi()
    {
        // An HttpContext with an anonymous ClaimsPrincipal (Identity.IsAuthenticated == false)
        // must NOT be treated as a real caller — fall back to "api" the same
        // way we do when no HttpContext is present at all.
        var fixture = new Fixture();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("anon-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
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
        public UnitCreationService Service { get; }

        public Fixture()
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

            // A no-op scope factory keeps tests free of a real DbContext —
            // the service's seed-expertise persistence branch only fires when
            // a manifest carries `expertise:`, which these tests never do.
            var scopeFactory = Substitute.For<IServiceScopeFactory>();

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
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var members = new[]
        {
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
            new MemberManifest { Agent = "qa-engineer" },
        };

        var result = await fixture.CreateFromManifestAsync("eng-team", members);

        result.MembersAdded.ShouldBe(3);

        foreach (var m in members)
        {
            await fixture.MembershipRepository.Received(1).UpsertAsync(
                Arg.Is<UnitMembership>(u =>
                    u.UnitId == "eng-team"
                    && u.AgentAddress == m.Agent
                    && u.Enabled
                    && u.Model == null
                    && u.Specialty == null
                    && u.ExecutionMode == null),
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task CreateFromManifestAsync_UnitTypedMember_DoesNotWriteMembershipRow()
    {
        // Per #217 scope: unit-typed members stay in actor state only — the
        // membership table is agent-addressed and polymorphic rows are a
        // future issue. The template fix must not leak unit rows into the
        // table through the same code path.
        var fixture = new Fixture();
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
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "sub-team"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_MembershipRepositoryThrows_ActorStateUpdatedWithWarning()
    {
        // Preferred failure mode per the fix plan: if the DB write fails
        // after the actor-state write, we log + surface a warning rather
        // than trying to roll back the actor state. Actor state is the
        // authoritative fast-path; a reconciler repairs divergence.
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.MembershipRepository
            .UpsertAsync(Arg.Any<UnitMembership>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("db down")));

        var result = await fixture.CreateFromManifestAsync(
            "flaky-unit",
            new[] { new MemberManifest { Agent = "lonely-agent" } });

        // The actor-state add succeeded — tally reflects it.
        result.MembersAdded.ShouldBe(1);
        await fixture.Proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "lonely-agent"),
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
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var members = new[]
        {
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
            new MemberManifest { Agent = "qa-engineer" },
        };

        var result = await fixture.CreateFromManifestAsync("eng-team", members);

        result.MembersAdded.ShouldBe(3);

        // Each agent member should have a Resolve check + Register call.
        foreach (var m in members)
        {
            await fixture.Directory.Received().ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == m.Agent),
                Arg.Any<CancellationToken>());
            await fixture.Directory.Received().RegisterAsync(
                Arg.Is<DirectoryEntry>(e =>
                    e.Address.Scheme == "agent"
                    && e.Address.Path == m.Agent
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
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        // Pre-register "tech-lead" so the Resolve returns non-null.
        var existingEntry = new DirectoryEntry(
            new Address("agent", "tech-lead"),
            Guid.NewGuid().ToString(),
            "tech-lead",
            "already exists",
            null,
            DateTimeOffset.UtcNow);
        fixture.Directory
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "tech-lead"),
                Arg.Any<CancellationToken>())
            .Returns(existingEntry);

        var members = new[]
        {
            new MemberManifest { Agent = "tech-lead" },
            new MemberManifest { Agent = "backend-engineer" },
        };

        var result = await fixture.CreateFromManifestAsync("eng-team-idem", members);

        result.MembersAdded.ShouldBe(2);

        // tech-lead was resolved as non-null so the auto-register skips it —
        // no RegisterAsync call should be made for agent://tech-lead.
        await fixture.Directory.DidNotReceive().RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent"
                && e.Address.Path == "tech-lead"),
            Arg.Any<CancellationToken>());

        // backend-engineer resolved as null (default) so it gets registered.
        await fixture.Directory.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e =>
                e.Address.Scheme == "agent"
                && e.Address.Path == "backend-engineer"),
            Arg.Any<CancellationToken>());
    }

    // --- T-05 (#947): differentiated creation — Draft vs Validating ---

    [Fact]
    public async Task CreateAsync_FullConfig_TransitionsToValidating()
    {
        // Full execution config (model + provider) + a resolvable credential
        // must send the unit straight into Validating so the Dapr
        // UnitValidationWorkflow kicks off the in-container probe.
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.CredentialResolver
            .ResolveAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);
        fixture.CredentialResolver
            .ResolveAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-model-unit");

        result.Unit.Status.ShouldBe(UnitStatus.Draft);
        await fixture.Proxy.DidNotReceive().TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());
    }
}