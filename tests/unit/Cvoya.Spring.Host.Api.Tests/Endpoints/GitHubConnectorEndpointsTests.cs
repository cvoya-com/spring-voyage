// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the GitHub connector's typed surface under
/// <c>/api/v1/connectors/github</c> — the typed per-unit config
/// (GET/PUT) and the connector-scoped actions (<c>list-installations</c>,
/// <c>install-url</c>). Uses its own factory so it can drive
/// <see cref="GitHubConnectorOptions"/> and
/// <see cref="IGitHubInstallationsClient"/> per test.
/// </summary>
public class GitHubConnectorEndpointsTests
{
    [Fact]
    public async Task ListInstallations_HappyPath_ReturnsProjection()
    {
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });

        await using var factory = CreateFactory(installationsClient: installationsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubInstallationResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(2);
        body[0].InstallationId.ShouldBe(1001L);
        body[0].Account.ShouldBe("acme");
    }

    [Fact]
    public async Task ListInstallations_Throws_Returns502()
    {
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("github 500"));

        await using var factory = CreateFactory(installationsClient: installationsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task GetInstallUrl_AppSlugConfigured_ReturnsInstallUrl()
    {
        await using var factory = CreateFactory(appSlug: "spring-voyage-test");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/actions/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubInstallUrlResponse>(ct);
        body.ShouldNotBeNull();
        body!.Url.ShouldBe("https://github.com/apps/spring-voyage-test/installations/new");
    }

    [Fact]
    public async Task GetInstallUrl_NoAppSlug_Returns502()
    {
        await using var factory = CreateFactory(appSlug: string.Empty);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/actions/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task PutConfig_UpsertsBinding()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var request = new UnitGitHubConfigRequest(
            "acme", "platform", AppInstallationId: 1001, Events: new[] { "issues" });

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await configStore.Received(1).SetAsync(
            "u1",
            GitHubConnectorType.GitHubTypeId,
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutConfig_PersistsReviewer()
    {
        // #1133: the new Reviewer field must round-trip through the
        // typed config endpoint and end up in the JSON the config store
        // sees. A whitespace-only Reviewer must collapse to null so the
        // PR-review skill never tries to assign an empty login.
        var captured = default(JsonElement?);
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        configStore.SetAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Do<JsonElement>(j => captured = j.Clone()),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var request = new UnitGitHubConfigRequest(
            "acme", "platform", AppInstallationId: 1001, Reviewer: "octocat");

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.ShouldNotBeNull();
        captured!.Value.GetProperty("reviewer").GetString().ShouldBe("octocat");

        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.Reviewer.ShouldBe("octocat");
    }

    [Fact]
    public async Task PutConfig_PersistsLabelRules()
    {
        var captured = default(JsonElement?);
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        configStore.SetAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Do<JsonElement>(j => captured = j.Clone()),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var request = new UnitGitHubConfigRequest(
            "acme",
            "platform",
            AddOnAssign: new[] { "in-progress" },
            RemoveOnAssign: new[] { "agent:backend" });

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.ShouldNotBeNull();
        captured!.Value.GetProperty("add_on_assign").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(new[] { "in-progress" });
        captured.Value.GetProperty("remove_on_assign").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(new[] { "agent:backend" });

        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.AddOnAssign.ShouldBe(new[] { "in-progress" });
        body.RemoveOnAssign.ShouldBe(new[] { "agent:backend" });
    }

    [Fact]
    public async Task PutConfig_BlankReviewer_StoresNull()
    {
        // Whitespace-only reviewer must persist as null. Otherwise a stray
        // " " would later be sent verbatim to GitHub's request-review API.
        var captured = default(JsonElement?);
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        configStore.SetAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Do<JsonElement>(j => captured = j.Clone()),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var request = new UnitGitHubConfigRequest(
            "acme", "platform", Reviewer: "   ");

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.ShouldNotBeNull();
        captured!.Value.GetProperty("reviewer").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetConfig_UnboundUnit_Returns404()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        configStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConfig_Bound_ReturnsTypedConfig()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var stored = JsonSerializer.SerializeToElement(
            new UnitGitHubConfig(
                "acme",
                "platform",
                1001,
                new[] { "issues" },
                AddOnAssign: new[] { "in-progress" },
                RemoveOnAssign: new[] { "agent:backend" }));
        configStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.Owner.ShouldBe("acme");
        body.Repo.ShouldBe("platform");
        body.AppInstallationId.ShouldBe(1001);
        body.Events.ShouldContain("issues");
        body.AddOnAssign.ShouldBe(new[] { "in-progress" });
        body.RemoveOnAssign.ShouldBe(new[] { "agent:backend" });
        // #1146: an explicit Events list must surface as eventsAreDefault: false
        // so the portal tab renders the per-event row enabled (not the
        // informational "use defaults" mode).
        body.EventsAreDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task GetConfig_NoExplicitEvents_ReportsEventsAreDefault()
    {
        // #1146: when the persisted binding has no explicit Events list
        // (null sentinel — the wizard's "Connector defaults" path), the
        // response surfaces the connector defaults verbatim AND sets
        // EventsAreDefault: true so the post-bind tab renders with the
        // toggle checked and the per-event row disabled.
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var stored = JsonSerializer.SerializeToElement(
            new UnitGitHubConfig("acme", "platform", 1001, Events: null));
        configStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.EventsAreDefault.ShouldBeTrue();
        body.Events.ShouldBe(new[] { "issues", "pull_request", "issue_comment" });
    }

    [Fact]
    public async Task GetConfig_ExplicitDefaultEqualSet_IsNotFlippedToDefault()
    {
        // #1146: the contract change exists precisely so an operator who
        // deliberately picks the same set as the connector defaults is
        // not silently re-rendered as "use defaults". Anti-regression
        // for the rejected client-side heuristic option.
        var explicitDefaults = new[] { "issues", "pull_request", "issue_comment" };
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var stored = JsonSerializer.SerializeToElement(
            new UnitGitHubConfig("acme", "platform", 1001, explicitDefaults));
        configStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.EventsAreDefault.ShouldBeFalse();
        body.Events.ShouldBe(explicitDefaults);
    }

    [Fact]
    public async Task PutConfig_NullEvents_RoundTripsAsEventsAreDefault()
    {
        // #1146: putting a config with no Events (the wizard's "Connector
        // defaults" wire shape) must round-trip — the response from the
        // very same PUT call already carries EventsAreDefault: true so a
        // tab refresh after save renders with the toggle still checked
        // and the row disabled.
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var request = new UnitGitHubConfigRequest("acme", "platform", AppInstallationId: 1001);

        var response = await client.PutAsJsonAsync(
            "/api/v1/tenant/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.EventsAreDefault.ShouldBeTrue();
        body.Events.ShouldBe(new[] { "issues", "pull_request", "issue_comment" });
    }

    [Fact]
    public async Task ListInstallations_ConnectorDisabled_Returns404WithStructuredBody()
    {
        // Regression for #609. When the GitHub App private key / app id were
        // missing at startup, AddCvoyaSpringConnectorGitHub registers the
        // connector in a disabled-with-reason state. The endpoint must short-
        // circuit before it reaches IGitHubInstallationsClient (which is
        // guaranteed to fail with "No supported key formats were found" on
        // JWT sign) and emit a structured body the portal (PR #610) can
        // render cleanly — not a 502.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("should not be called"));

        // Disabled is driven by absent options — the credential requirement
        // reports Disabled, and the connector endpoints short-circuit. No
        // more IGitHubConnectorAvailability override (the interface was
        // removed in #616).
        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            appEnabled: false);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        body.TryGetProperty("disabled", out var disabled).ShouldBeTrue(
            "portal/CLI key off the `disabled` extension to render the configuration banner");
        disabled.GetBoolean().ShouldBeTrue();

        body.TryGetProperty("reason", out var reason).ShouldBeTrue();
        var reasonString = reason.GetString();
        reasonString.ShouldNotBeNull();
        reasonString!.ShouldContain("GitHub App not configured");

        body.TryGetProperty("detail", out var detail).ShouldBeTrue();
        var detailString = detail.GetString();
        detailString.ShouldNotBeNull();
        detailString!.ShouldContain("GitHub App not configured");

        // The installations client must NOT have been invoked — the whole
        // point of the fix is to skip the hot path.
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInstallUrl_ConnectorDisabled_Returns404WithStructuredBody()
    {
        // Same short-circuit as list-installations so the portal's install-
        // app link renders a consistent "not configured" state rather than
        // a partial mixed response.
        await using var factory = CreateFactory(appEnabled: false);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("disabled").GetBoolean().ShouldBeTrue();
        var reasonString = body.GetProperty("reason").GetString();
        reasonString.ShouldNotBeNull();
        reasonString!.ShouldContain("GitHub App not configured");
    }

    [Fact]
    public async Task ListRepositories_AggregatesAcrossInstallations_ForSessionedUser()
    {
        // #1663: the endpoint is fail-closed against session-less callers,
        // so the happy-path "aggregates across installations" baseline
        // must run with a real OAuth session in scope. We model a user
        // ("alice") who belongs to "acme" and to her own personal
        // account; all of the App's installations happen to fall in
        // that scope, so the user-scoped intersect doesn't drop any.
        const string sessionId = "test-session-aggregate";
        const string fakeStoreKey = "store-key-aggregate";
        const string fakeAccessToken = "ghu_aggregate";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo read:org",
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(fakeAccessToken));

        // #1133: the new endpoint replaces "type owner / type repo /
        // pick installation" with a single dropdown sourced from every
        // visible installation. The response carries the installation id
        // back so the wizard never has to re-resolve it on submit.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1001L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(10L, "acme", "platform", "acme/platform", true),
                new GitHubInstallationRepository(11L, "acme", "ui", "acme/ui", false),
            });
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1002L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(20L, "alice", "demos", "alice/demos", false),
            });

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(3);

        // Stable alphabetical order — keeps the dropdown from shuffling
        // between renders.
        body.Select(r => r.FullName).ToArray().ShouldBe(
            new[] { "acme/platform", "acme/ui", "alice/demos" });

        // Each row carries its installation id so the wizard never has
        // to call back to resolve (owner, repo) → installation.
        body.Single(r => r.FullName == "acme/platform").InstallationId.ShouldBe(1001L);
        body.Single(r => r.FullName == "alice/demos").InstallationId.ShouldBe(1002L);

        // Owner / repo are split for direct round-trip into UnitGitHubConfig.
        var platform = body.Single(r => r.FullName == "acme/platform");
        platform.Owner.ShouldBe("acme");
        platform.Repo.ShouldBe("platform");
        platform.Private.ShouldBeTrue();

        // The App-installation listing must NEVER be used when an OAuth
        // user token is in play (#1663). Only the user-scoped
        // /user/installations/{id}/repositories path is allowed.
        await installationsClient.DidNotReceive()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_PerInstallationFailureDoesNotPoisonList()
    {
        // One installation throwing must not collapse the entire response
        // — the wizard still needs to render the other installations'
        // repos so the user can pick one.
        const string sessionId = "test-session-poison";
        const string fakeStoreKey = "store-key-poison";
        const string fakeAccessToken = "ghu_poison";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo read:org",
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(fakeAccessToken));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1001L, fakeAccessToken, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("github 503"));
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1002L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(20L, "alice", "demos", "alice/demos", false),
            });

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(1);
        body[0].FullName.ShouldBe("alice/demos");
    }

    [Fact]
    public async Task ListRepositories_ConnectorDisabled_Returns404WithStructuredBody()
    {
        await using var factory = CreateFactory(appEnabled: false);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-repositories", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("disabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ListCollaborators_HappyPath_ReturnsLogins()
    {
        var collaboratorsClient = Substitute.For<IGitHubCollaboratorsClient>();
        collaboratorsClient.ListCollaboratorsAsync(
                1001L, "acme", "platform", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubCollaborator("octocat", "https://avatars/octocat"),
                new GitHubCollaborator("hubot", null),
            });

        await using var factory = CreateFactory(collaboratorsClient: collaboratorsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-collaborators?installation_id=1001&owner=acme&repo=platform",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubCollaboratorResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(2);
        body.Select(c => c.Login).ToArray().ShouldBe(new[] { "octocat", "hubot" });
    }

    [Fact]
    public async Task ListCollaborators_MissingParams_Returns400()
    {
        // Defence in depth — the client is responsible for not omitting
        // these, but the endpoint must reject the call before reaching
        // any GitHub API.
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-collaborators?installation_id=0&owner=&repo=",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListCollaborators_ConnectorDisabled_Returns404WithStructuredBody()
    {
        await using var factory = CreateFactory(appEnabled: false);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-collaborators?installation_id=1001&owner=acme&repo=platform",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("disabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetConfigSchema_ReturnsJsonSchema()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/config-schema", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.TryGetProperty("type", out var type).ShouldBeTrue();
        type.GetString().ShouldBe("object");
        body.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("owner");
        var properties = body.GetProperty("properties");
        properties.TryGetProperty("add_on_assign", out _).ShouldBeTrue();
        properties.TryGetProperty("remove_on_assign", out _).ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // #1505 — user-scoped list-repositories (tenant-isolation)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ListRepositories_WithSessionId_OrgInstallationReposIncludedWithoutReadOrgScope()
    {
        // The App is installed on both the user's personal account and on an
        // org ("cvoya-com"). The user is an admin/member of the org but the
        // OAuth token does NOT have the read:org scope (Scopes="repo").
        // Previously a pre-filter using GET /user/orgs dropped the org
        // installation because private org membership requires read:org.
        // With the fix, GitHub's own per-installation endpoint enforces
        // access: the org installation returns the repos the user can see,
        // regardless of whether read:org is in the token's scope.
        const string sessionId = "test-session-org-no-readorg";
        const string fakeStoreKey = "store-key-org-fix";
        const string fakeAccessToken = "ghu_org_fix";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo",  // no read:org
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(fakeAccessToken));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(3001L, "alice", "User", "all"),
                new GitHubInstallation(3002L, "cvoya-com", "Organization", "all"),
            });
        installationsClient.ListUserAccessibleRepositoriesAsync(
                3001L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(30L, "alice", "my-repo", "alice/my-repo", false),
            });
        // Even without read:org, GitHub's per-installation endpoint returns
        // the org repos the user has access to (as a member/admin).
        installationsClient.ListUserAccessibleRepositoriesAsync(
                3002L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(31L, "cvoya-com", "platform", "cvoya-com/platform", false),
                new GitHubInstallationRepository(32L, "cvoya-com", "private-repo", "cvoya-com/private-repo", true),
            });

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(3);
        body.Select(r => r.FullName).ToArray().ShouldBe(
            new[] { "alice/my-repo", "cvoya-com/platform", "cvoya-com/private-repo" });
    }

    [Fact]
    public async Task ListRepositories_WithSessionId_OrgReposAreNotLeakedToNonMembers()
    {
        // Two installations: one belonging to "acme" (an org the user can
        // access) and one belonging to "other-org" (a different tenant the
        // user has no access to). The endpoint calls
        // GET /user/installations/{id}/repositories for every installation
        // and lets GitHub enforce access control server-side. For "other-org",
        // the user's token returns no repos; for "acme" it returns the repos
        // the user can see. The result must only contain "acme" repos.
        const string sessionId = "test-session-abc";
        const string fakeAccessToken = "ghu_faketoken";
        const string fakeStoreKey = "store-key-123";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo",
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(fakeAccessToken));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        // App sees two installations: one for acme, one for another tenant.
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "all"),
                new GitHubInstallation(1002L, "other-org", "Organization", "all"),
            });
        // User can access acme repos.
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1001L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(10L, "acme", "platform", "acme/platform", false),
            });
        // GitHub returns empty for other-org — user is not a member.
        installationsClient.ListUserAccessibleRepositoriesAsync(
                1002L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GitHubInstallationRepository>());

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse[]>(ct);
        body.ShouldNotBeNull();

        // Only the acme installation's repos should appear.
        body!.Length.ShouldBe(1);
        body[0].FullName.ShouldBe("acme/platform");
        body[0].InstallationId.ShouldBe(1001L);

        // The App-installation-token path must not be used when an OAuth
        // user token is available — that's the leak #1663 closes.
        await installationsClient.DidNotReceive()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_WithSessionId_IncludesPersonalAccountInstallation()
    {
        // The user has a personal installation (account == their login) and
        // there is an unrelated org installation. The endpoint calls
        // GET /user/installations/{id}/repositories for all installations;
        // personal repos come back for alice's installation, and GitHub
        // returns empty for other-corp (user has no access). Only alice's
        // repos should appear.
        const string sessionId = "test-session-personal";
        const string fakeStoreKey = "store-key-personal";
        const string fakeAccessToken = "ghu_personal";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo",
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(fakeAccessToken));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(2001L, "alice", "User", "all"),
                new GitHubInstallation(2002L, "other-corp", "Organization", "all"),
            });
        installationsClient.ListUserAccessibleRepositoriesAsync(
                2001L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(20L, "alice", "dotfiles", "alice/dotfiles", false),
            });
        // GitHub returns empty for other-corp — user is not a member.
        installationsClient.ListUserAccessibleRepositoriesAsync(
                2002L, fakeAccessToken, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GitHubInstallationRepository>());

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GitHubRepositoryResponse[]>(ct);
        body.ShouldNotBeNull();
        body!.Length.ShouldBe(1);
        body[0].FullName.ShouldBe("alice/dotfiles");
    }

    [Fact]
    public async Task ListRepositories_WithUnknownSessionId_Returns401MissingOAuth()
    {
        // #1663: an unknown session id is treated the same as a missing
        // one — the endpoint must NEVER fall back to the App-installation
        // listing. The pre-#1663 contract returned every visible
        // installation in this case, leaking repos the caller has no
        // user-side permission for.
        const string sessionId = "unknown-session";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OAuthSession?>(null));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Installations must not be enumerated when no OAuth session is available."));

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("missingOAuth").GetBoolean().ShouldBeTrue(
            "the portal keys its remediation panel off the missingOAuth flag");
        body.GetProperty("reason").GetString().ShouldNotBeNullOrEmpty();

        // No installation lookup may have happened — that's the leak we
        // closed.
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_WithoutSessionId_Returns401MissingOAuth()
    {
        // #1663: the endpoint is fail-closed against session-less callers.
        // The pre-#1663 contract returned the full installation list
        // here, which surfaced every repo the App could see — including
        // ones the caller's GitHub identity has no permission to view.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Installations must not be enumerated when no OAuth session is supplied."));

        await using var factory = CreateFactory(installationsClient: installationsClient);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/actions/list-repositories", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("missingOAuth").GetBoolean().ShouldBeTrue();
        body.GetProperty("reason").GetString().ShouldNotBeNullOrEmpty();

        // The installation list MUST NOT be enumerated — that's the
        // entire point of the fail-closed contract.
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_SessionWithoutAccessToken_Returns401MissingOAuth()
    {
        // #1663: when the OAuth session record exists but the secret
        // store has no token (e.g. the secret was rotated / wiped),
        // the endpoint must fail closed rather than fall back. The
        // pre-#1663 code logged a warning and returned the unfiltered
        // installation list.
        const string sessionId = "session-without-token";
        const string fakeStoreKey = "store-key-empty";

        var sessionStore = Substitute.For<IOAuthSessionStore>();
        sessionStore.GetAsync(sessionId, Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: sessionId,
                Login: "alice",
                UserId: 42L,
                Scopes: "repo read:org",
                AccessTokenStoreKey: fakeStoreKey,
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: null));

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync(fakeStoreKey, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "Installations must not be enumerated without a usable OAuth token."));

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            sessionStore: sessionStore,
            secretStore: secretStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            $"/api/v1/tenant/connectors/github/actions/list-repositories?session_id={sessionId}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("missingOAuth").GetBoolean().ShouldBeTrue();

        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Wraps <see cref="CustomWebApplicationFactory"/> with per-test
    /// overrides of <see cref="GitHubConnectorOptions"/>,
    /// <see cref="IGitHubInstallationsClient"/>, and the connector config
    /// store. Re-registers the real <see cref="GitHubConnectorType"/> so the
    /// typed surface is exercised rather than the shared factory's stub.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string? appSlug = null,
        IGitHubInstallationsClient? installationsClient = null,
        IGitHubCollaboratorsClient? collaboratorsClient = null,
        IUnitConnectorConfigStore? configStore = null,
        bool appEnabled = true,
        IOAuthSessionStore? sessionStore = null,
        ISecretStore? secretStore = null,
        IGitHubUserScopeResolver? scopeResolver = null)
    {
        var baseFactory = new CustomWebApplicationFactory();
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Post-configure the GitHub options so the credential
                // requirement sees what the test wants to see. Enabled =
                // inline a valid PEM + numeric AppId (via a fresh RSA key),
                // disabled = leave the defaults (empty) so the requirement
                // reports Disabled. This drives the endpoint short-circuit
                // through the real IConfigurationRequirement path that
                // replaced the pre-#616 IGitHubConnectorAvailability seam.
                if (appEnabled)
                {
                    var pem = System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem();
                    services.PostConfigure<GitHubConnectorOptions>(opts =>
                    {
                        opts.AppId = 12345;
                        opts.PrivateKeyPem = pem;
                        opts.WebhookSecret = "test-secret";
                    });
                }

                if (appSlug is not null)
                {
                    services.PostConfigure<GitHubConnectorOptions>(opts => opts.AppSlug = appSlug);
                }

                // Drop the stub IConnectorType registered by the shared factory
                // so the real GitHubConnectorType owns the /connectors/github
                // routes.
                var connectorTypeDescriptors = services
                    .Where(d => d.ServiceType == typeof(IConnectorType))
                    .ToList();
                foreach (var d in connectorTypeDescriptors)
                {
                    services.Remove(d);
                }

                // Issue #2456 removed IGitHubWebhookRegistrar — the
                // platform no longer creates per-repo hooks. The connector
                // type no longer depends on it.

                if (installationsClient is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IGitHubInstallationsClient))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(installationsClient);
                }

                if (collaboratorsClient is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IGitHubCollaboratorsClient))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(collaboratorsClient);
                }

                if (configStore is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IUnitConnectorConfigStore))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(configStore);
                }

                // #1505: optional OAuth session store override for user-scope tests.
                if (sessionStore is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IOAuthSessionStore))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(sessionStore);
                }

                // #1505: optional secret store override. The base factory's stub
                // ReadAsync always returns null; tests that exercise the OAuth-token
                // read path need to supply a pre-configured one.
                if (secretStore is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(ISecretStore))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(secretStore);
                }

                // #1505: optional user-scope resolver override.
                if (scopeResolver is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IGitHubUserScopeResolver))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(scopeResolver);
                }

                services.AddSingleton<GitHubConnectorType>();
                services.AddSingleton<IConnectorType>(
                    sp => sp.GetRequiredService<GitHubConnectorType>());
            });
        });
    }
}
