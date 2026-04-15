// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the manifest-backed unit creation endpoints
/// (<c>POST /api/v1/units/from-yaml</c> and <c>POST /api/v1/units/from-template</c>)
/// plus the <c>GET /api/v1/packages/templates</c> catalog endpoint.
/// </summary>
public class UnitCreationEndpointTests : IClassFixture<UnitCreationEndpointTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public UnitCreationEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task FromYaml_CreatesUnit_AddsMembers_ReturnsWarningsForUnsupportedSections()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci => new DirectoryEntry(
                ci.Arg<Address>(),
                "actor-1",
                "actor-1",
                string.Empty,
                null,
                DateTimeOffset.UtcNow));

        const string Yaml = """
            unit:
              name: from-yaml-unit
              description: A unit created via YAML.
              ai:
                agent: claude
                model: claude-sonnet-4-20250514
              members:
                - agent: tech-lead
                - agent: backend-engineer
              policies:
                communication: through-unit
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest(Yaml, DisplayName: "From YAML"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("unit").GetProperty("name").GetString().ShouldBe("from-yaml-unit");
        doc.RootElement.GetProperty("unit").GetProperty("displayName").GetString().ShouldBe("From YAML");
        doc.RootElement.GetProperty("unit").GetProperty("model").GetString().ShouldBe("claude-sonnet-4-20250514");
        // #324: the manifest declared two members; the service now calls the
        // unit actor directly (bypassing MessageRouter's permission gate) so
        // both adds should succeed and be reflected in membersAdded. Each
        // AddMemberAsync call on the proxy also gets tallied below.
        doc.RootElement.GetProperty("membersAdded").GetInt32().ShouldBe(2);

        var warnings = doc.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetString())
            .ToList();
        warnings.ShouldContain(w => w!.Contains("ai"));
        warnings.ShouldContain(w => w!.Contains("policies"));

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Path == "from-yaml-unit"),
            Arg.Any<CancellationToken>());
        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "claude-sonnet-4-20250514"),
            Arg.Any<CancellationToken>());
        await proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "tech-lead"),
            Arg.Any<CancellationToken>());
        await proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "backend-engineer"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromYaml_InvalidYaml_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest("not-a-unit: 1"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FromYaml_EmptyBody_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest(string.Empty),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PackagesTemplates_ReturnsDiscoveredYamlFiles()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/packages/templates", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var entries = doc.RootElement.EnumerateArray().ToList();
        entries.ShouldNotBeEmpty();
        entries.ShouldContain(e =>
            e.GetProperty("package").GetString() == "sample-pkg"
            && e.GetProperty("name").GetString() == "sample-unit");
    }

    [Fact]
    public async Task FromTemplate_UnknownTemplate_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-template",
            new CreateUnitFromTemplateRequest("does-not-exist", "nope"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FromTemplate_KnownTemplate_CreatesUnitFromYaml()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-template",
            new CreateUnitFromTemplateRequest("sample-pkg", "sample-unit"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("unit").GetProperty("name").GetString().ShouldBe("sample-unit");
        // #324: sample-unit.yaml declares one member (sample-agent). Direct-
        // proxy adds mean the count now reflects the manifest verbatim.
        doc.RootElement.GetProperty("membersAdded").GetInt32().ShouldBe(1);

        await proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "sample-agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromTemplate_GrantsCreatorOwnerOnNewUnit()
    {
        // #324 Fix A: the creator of a unit must be granted Owner on the
        // fresh unit before any member-add runs. The LocalDev auth handler
        // planted on the test host surfaces AuthConstants.DefaultLocalUserId
        // as the NameIdentifier claim, so that id is what should land on
        // the unit actor's human-permission map.
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-template",
            new CreateUnitFromTemplateRequest("sample-pkg", "sample-unit"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The Owner grant lands on the authenticated subject from the
        // LocalDev handler — NOT on the synthetic "api" fallback.
        await proxy.Received(1).SetHumanPermissionAsync(
            AuthConstants.DefaultLocalUserId,
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == AuthConstants.DefaultLocalUserId
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
        await proxy.DidNotReceive().SetHumanPermissionAsync(
            "api",
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_FromScratch_NoMembers_GrantsCreatorOwner()
    {
        // #324 Fix A, coverage for the from-scratch path: even with zero
        // members declared, the creator still becomes Owner so subsequent
        // HTTP calls through MessageRouter pass the permission gate.
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new
        {
            name = "scratch-owner-unit",
            displayName = "Scratch Owner Unit",
            description = "",
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        // CreateAsync returns a bare UnitResponse (no wrapper) for #192
        // compatibility — no membersAdded here, but AddMemberAsync must not
        // have been invoked because the scratch request carries no members.
        doc.RootElement.TryGetProperty("membersAdded", out _).ShouldBeFalse();

        await proxy.DidNotReceive().AddMemberAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
        await proxy.Received(1).SetHumanPermissionAsync(
            AuthConstants.DefaultLocalUserId,
            Arg.Is<UnitPermissionEntry>(e => e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromTemplate_AddsMembersViaDirectProxy_NotMessageRouter()
    {
        // #324 Fix B: the service used to dispatch each AddMember as a
        // domain message through MessageRouter, where a freshly created
        // unit's empty permission map denied the call. The fix is to call
        // the unit actor directly. This test asserts the behaviour change:
        // proxy.AddMemberAsync is invoked exactly once per manifest member.
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-template",
            new CreateUnitFromTemplateRequest("sample-pkg", "sample-unit"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("membersAdded").GetInt32().ShouldBe(1);

        await proxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "sample-agent"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_ScratchNoConnector_NoSecrets_Succeeds()
    {
        // Bug #261 reproduction at the API layer: the wizard's "scratch +
        // skip connector" path posts to /api/v1/units with no `connector`
        // field at all. The endpoint must succeed without invoking the
        // connector store, the bundle store, or any rollback.
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new
        {
            name = "scratch-skip-unit",
            displayName = "Scratch Skip Unit",
            description = "made via wizard scratch + skip",
            model = "claude-sonnet-4-20250514",
            color = "#6366f1",
            // No 'connector' field — the wizard omits it for the skip case.
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Path == "scratch-skip-unit"),
            Arg.Any<CancellationToken>());
        // The connector store must NOT have been touched.
        await _factory.ConnectorConfigStore.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        // No rollback occurred.
        await _factory.DirectoryService.DidNotReceive().UnregisterAsync(
            Arg.Any<Address>(), Arg.Any<CancellationToken>());
        // The model hint reached the actor metadata write.
        await proxy.Received(1).SetMetadataAsync(
            Arg.Is<UnitMetadata>(m => m.Model == "claude-sonnet-4-20250514"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_WithConnectorBinding_HappyPath_BindsAfterCreate()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var configPayload = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        var request = new
        {
            name = "bundled-unit",
            displayName = "Bundled Unit",
            description = "created + bound in one call",
            connector = new
            {
                typeSlug = "stub",
                typeId = "00000000-0000-0000-0000-00000000beef",
                config = configPayload,
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Path == "bundled-unit"),
            Arg.Any<CancellationToken>());
        await _factory.ConnectorConfigStore.Received(1).SetAsync(
            "bundled-unit",
            new Guid("00000000-0000-0000-0000-00000000beef"),
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_WithUnknownConnector_Returns404_AndRollsBack()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new
        {
            name = "missing-connector-unit",
            displayName = "X",
            description = "",
            connector = new
            {
                typeSlug = "does-not-exist",
                typeId = "00000000-0000-0000-0000-000000000000",
                config = JsonSerializer.SerializeToElement(new { }),
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Validation is up-front — nothing should have been written or
        // unregistered.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
        await _factory.ConnectorConfigStore.DidNotReceive().SetAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_ConnectorBindingStoreFailure_RollsBackUnit()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _factory.ConnectorConfigStore
            .SetAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("store is down"));

        var configPayload = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "platform" });
        var request = new
        {
            name = "rollback-unit",
            displayName = "Rollback Unit",
            description = "",
            connector = new
            {
                typeSlug = "stub",
                typeId = "00000000-0000-0000-0000-00000000beef",
                config = configPayload,
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);

        // The unit was registered, then rolled back via UnregisterAsync.
        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Path == "rollback-unit"),
            Arg.Any<CancellationToken>());
        await _factory.DirectoryService.Received(1).UnregisterAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "rollback-unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateUnit_BindingRequestWithNoIdentifier_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var request = new
        {
            name = "invalid-binding-unit",
            displayName = "X",
            description = "",
            connector = new
            {
                typeSlug = (string?)null,
                typeId = "00000000-0000-0000-0000-000000000000",
                config = JsonSerializer.SerializeToElement(new { }),
            },
        };

        var response = await _client.PostAsJsonAsync("/api/v1/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromYaml_WithResolvableSkillBundle_CreatesUnitAndPersistsBundle()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        const string Yaml = """
            unit:
              name: bundle-unit
              ai:
                skills:
                  - package: spring-voyage/sample-pkg
                    skill: demo
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest(Yaml),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The bundle store was called with the resolved bundle. Captured via
        // the IStateStore mock that StateStoreBackedUnitSkillBundleStore
        // writes to. Matching is key-only; NSubstitute's generic-method
        // matcher on SetAsync<T>(...) requires the concrete TValue type, so
        // we query the received calls list directly.
        var setCalls = _factory.StateStore.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetAsync")
            .Select(c => c.GetArguments())
            .Where(args => args.Length >= 1 && (args[0] as string) == "Unit:SkillBundles:bundle-unit")
            .ToList();
        setCalls.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task FromYaml_WithUnresolvableSkillBundle_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        const string Yaml = """
            unit:
              name: missing-bundle
              ai:
                skills:
                  - package: spring-voyage/does-not-exist
                    skill: ghost
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest(Yaml),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Validation fails up-front; nothing should have been written.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromYaml_WithBundleRequiringUnavailableTool_CreatesUnit_WithWarning()
    {
        // Per #261: bundles often declare aspirational unit-orchestration
        // tools (e.g. `assignToAgent`) that no connector surfaces. The
        // validator now returns these as advisory warnings and creation
        // proceeds — the agent will get a runtime "tool not found" if it
        // actually invokes the missing tool. See #306 for the follow-up.
        var ct = TestContext.Current.CancellationToken;

        ResetMocks();
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        const string Yaml = """
            unit:
              name: bundle-needs-tool
              ai:
                skills:
                  - package: spring-voyage/sample-pkg
                    skill: demo-with-tool
            """;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-yaml",
            new CreateUnitFromYamlRequest(Yaml),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var warnings = doc.RootElement.GetProperty("warnings")
            .EnumerateArray()
            .Select(w => w.GetString())
            .ToList();
        warnings.ShouldContain(w => w!.Contains("not_a_real_tool"));
        warnings.ShouldContain(w => w!.Contains("not surfaced by any registered connector"));

        // The unit was registered despite the missing-tool warning.
        await _factory.DirectoryService.Received(1).RegisterAsync(
            Arg.Is<DirectoryEntry>(e => e.Address.Path == "bundle-needs-tool"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FromTemplate_PathTraversalRejected()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/units/from-template",
            new CreateUnitFromTemplateRequest("..", "sample-unit"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ResetMocks()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.ConnectorConfigStore.ClearReceivedCalls();
        // Re-establish the happy-path default for ConnectorConfigStore.SetAsync.
        // Tests that want the call to throw must re-configure it explicitly
        // (see CreateUnit_ConnectorBindingStoreFailure_RollsBackUnit).
        _factory.ConnectorConfigStore
            .SetAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    /// <summary>
    /// Specialised factory that plants a tiny packages fixture on disk so the
    /// file-system catalog has something to enumerate.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public string PackagesRoot { get; } = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"packages-{Guid.NewGuid():N}");

        public Factory()
        {
            var pkgDir = Path.Combine(PackagesRoot, "sample-pkg", "units");
            Directory.CreateDirectory(pkgDir);
            File.WriteAllText(
                Path.Combine(pkgDir, "sample-unit.yaml"),
                """
                unit:
                  name: sample-unit
                  description: Sample template used by integration tests.
                  members:
                    - agent: sample-agent
                """);

            // Skill bundles for the #167 / C4 integration tests. The resolver
            // strips the 'spring-voyage/' namespace prefix so the on-disk
            // directory is just 'sample-pkg/skills'.
            var skillsDir = Path.Combine(PackagesRoot, "sample-pkg", "skills");
            Directory.CreateDirectory(skillsDir);

            // Prompt-only bundle — happy-path, no tool requirements.
            File.WriteAllText(
                Path.Combine(skillsDir, "demo.md"),
                "## Demo\nA minimal skill bundle for integration tests.");

            // Bundle with a tool requirement that no registered registry
            // surfaces — used to exercise the manifest-validation error path.
            File.WriteAllText(
                Path.Combine(skillsDir, "demo-with-tool.md"),
                "## Demo with tool\nRequires a tool that is not surfaced.");
            File.WriteAllText(
                Path.Combine(skillsDir, "demo-with-tool.tools.json"),
                """
                [
                  { "name": "not_a_real_tool", "description": "d", "parameters": {} }
                ]
                """);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                // Replace the auto-discovered options with our fixture root so
                // the catalog looks at a deterministic directory regardless of
                // where the test host is launched from.
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(PackageCatalogOptions))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton(new PackageCatalogOptions { Root = PackagesRoot });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                try
                {
                    if (Directory.Exists(PackagesRoot))
                    {
                        Directory.Delete(PackagesRoot, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup; ignore.
                }
            }
        }
    }
}