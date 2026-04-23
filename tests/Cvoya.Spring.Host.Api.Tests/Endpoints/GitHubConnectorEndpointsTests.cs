// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;

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
            "/api/v1/connectors/github/actions/list-installations", ct);

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
            "/api/v1/connectors/github/actions/list-installations", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task GetInstallUrl_AppSlugConfigured_ReturnsInstallUrl()
    {
        await using var factory = CreateFactory(appSlug: "spring-voyage-test");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/connectors/github/actions/install-url", ct);

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

        var response = await client.GetAsync("/api/v1/connectors/github/actions/install-url", ct);

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
            "/api/v1/connectors/github/units/u1/config", request, ct);

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
            "/api/v1/connectors/github/units/u1/config", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.ShouldNotBeNull();
        captured!.Value.GetProperty("reviewer").GetString().ShouldBe("octocat");

        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.Reviewer.ShouldBe("octocat");
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
            "/api/v1/connectors/github/units/u1/config", request, ct);

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

        var response = await client.GetAsync("/api/v1/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetConfig_Bound_ReturnsTypedConfig()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var stored = JsonSerializer.SerializeToElement(
            new UnitGitHubConfig("acme", "platform", 1001, new[] { "issues" }));
        configStore.GetAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, stored));

        await using var factory = CreateFactory(configStore: configStore);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/connectors/github/units/u1/config", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnitGitHubConfigResponse>(ct);
        body.ShouldNotBeNull();
        body!.Owner.ShouldBe("acme");
        body.Repo.ShouldBe("platform");
        body.AppInstallationId.ShouldBe(1001);
        body.Events.ShouldContain("issues");
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
            "/api/v1/connectors/github/actions/list-installations", ct);

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
            "/api/v1/connectors/github/actions/install-url", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("disabled").GetBoolean().ShouldBeTrue();
        var reasonString = body.GetProperty("reason").GetString();
        reasonString.ShouldNotBeNull();
        reasonString!.ShouldContain("GitHub App not configured");
    }

    [Fact]
    public async Task ListRepositories_AggregatesAcrossInstallations()
    {
        // #1133 + #1153: the endpoint scopes its result to the signed-in
        // GitHub user — pass a user access token through the provider so
        // the user-scoped install + repo enumeration runs. The response
        // carries the installation id back so the wizard never has to
        // re-resolve it on submit.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListUserAccessibleInstallationsAsync(
                "user-token", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });
        installationsClient.ListUserAccessibleInstallationRepositoriesAsync(
                "user-token", 1001L, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(10L, "acme", "platform", "acme/platform", true),
                new GitHubInstallationRepository(11L, "acme", "ui", "acme/ui", false),
            });
        installationsClient.ListUserAccessibleInstallationRepositoriesAsync(
                "user-token", 1002L, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(20L, "alice", "demos", "alice/demos", false),
            });

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            userAccessToken: "user-token");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/connectors/github/actions/list-repositories", ct);

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

        // #1153: the App-scoped enumeration MUST NOT run — the bug we
        // fixed was precisely that path leaking other users' repos.
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_PerInstallationFailureDoesNotPoisonList()
    {
        // One installation throwing must not collapse the entire response
        // — the wizard still needs to render the other installations'
        // repos so the user can pick one.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListUserAccessibleInstallationsAsync(
                "user-token", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(1001L, "acme", "Organization", "selected"),
                new GitHubInstallation(1002L, "alice", "User", "all"),
            });
        installationsClient.ListUserAccessibleInstallationRepositoriesAsync(
                "user-token", 1001L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("github 503"));
        installationsClient.ListUserAccessibleInstallationRepositoriesAsync(
                "user-token", 1002L, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(20L, "alice", "demos", "alice/demos", false),
            });

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            userAccessToken: "user-token");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/connectors/github/actions/list-repositories", ct);

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
            "/api/v1/connectors/github/actions/list-repositories", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("disabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ListRepositories_NoSignedInGitHubUser_Returns401WithRequiresSignin()
    {
        // #1153: when the request does not carry a signed-in GitHub user
        // identity, the endpoint MUST refuse to fall back to the
        // App-wide enumeration — that's the exact path that leaked
        // every user's repos. The response is a 401 with a structured
        // `requires_signin` extension the wizard renders as a "Sign in
        // with GitHub" CTA.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            userAccessToken: null);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/connectors/github/actions/list-repositories", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("requires_signin").GetBoolean().ShouldBeTrue();
        body.GetProperty("provider").GetString().ShouldBe("github");
        body.GetProperty("authorize_path").GetString()
            .ShouldBe("/api/v1/connectors/github/oauth/authorize");

        // Critical: the App-scoped path is the bug — it MUST NOT have
        // been touched, even on the no-user branch.
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(default, Arg.Any<CancellationToken>());
        await installationsClient.DidNotReceiveWithAnyArgs()
            .ListUserAccessibleInstallationsAsync(default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_UserTokenRejected_Returns401WithRequiresSignin()
    {
        // When GitHub rejects the user-to-server token (expired, revoked,
        // scope mismatch) the endpoint MUST surface that as 401 +
        // requires_signin — not a generic 502 — so the wizard re-prompts
        // for sign-in instead of leaving the operator stuck.
        var installationsClient = Substitute.For<IGitHubInstallationsClient>();
        installationsClient.ListUserAccessibleInstallationsAsync(
                "user-token", Arg.Any<CancellationToken>())
            .ThrowsAsync(new Octokit.AuthorizationException(
                new ResponseFake(HttpStatusCode.Unauthorized)));

        await using var factory = CreateFactory(
            installationsClient: installationsClient,
            userAccessToken: "user-token");
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/connectors/github/actions/list-repositories", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("requires_signin").GetBoolean().ShouldBeTrue();
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
            "/api/v1/connectors/github/actions/list-collaborators?installation_id=1001&owner=acme&repo=platform",
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
            "/api/v1/connectors/github/actions/list-collaborators?installation_id=0&owner=&repo=",
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
            "/api/v1/connectors/github/actions/list-collaborators?installation_id=1001&owner=acme&repo=platform",
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

        var response = await client.GetAsync("/api/v1/connectors/github/config-schema", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.TryGetProperty("type", out var type).ShouldBeTrue();
        type.GetString().ShouldBe("object");
        body.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldContain("owner");
    }

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
        string? userAccessToken = null)
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

                // Provide a webhook registrar substitute so GitHubConnectorType
                // construction succeeds; the tests in this class don't drive
                // /start / /stop.
                var regDescriptors = services
                    .Where(d => d.ServiceType == typeof(IGitHubWebhookRegistrar))
                    .ToList();
                foreach (var d in regDescriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton(Substitute.For<IGitHubWebhookRegistrar>());

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

                // #1153: stub the GitHub user-access-token resolver so
                // the test controls whether a signed-in GitHub user is
                // present on the request. A null token triggers the
                // requires_signin response path; a string flips into
                // the user-scoped enumeration.
                var providerDescriptors = services
                    .Where(d => d.ServiceType == typeof(IGitHubUserAccessTokenProvider))
                    .ToList();
                foreach (var d in providerDescriptors)
                {
                    services.Remove(d);
                }
                var userAccessProvider = Substitute.For<IGitHubUserAccessTokenProvider>();
                userAccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
                    .Returns(userAccessToken is null
                        ? null
                        : new GitHubUserAccess("octocat", 42L, userAccessToken));
                services.AddSingleton(userAccessProvider);

                services.AddSingleton<GitHubConnectorType>();
                services.AddSingleton<IConnectorType>(
                    sp => sp.GetRequiredService<GitHubConnectorType>());
            });
        });
    }

    /// <summary>
    /// Minimal fake of Octokit's <see cref="Octokit.IResponse"/> so the
    /// tests can raise <see cref="Octokit.AuthorizationException"/> /
    /// <see cref="Octokit.ApiException"/> without going through the real
    /// HTTP stack.
    /// </summary>
    private sealed class ResponseFake(HttpStatusCode statusCode) : Octokit.IResponse
    {
        public object Body => string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; }
            = new Dictionary<string, string>();

        public Octokit.ApiInfo ApiInfo { get; } = new Octokit.ApiInfo(
            new Dictionary<string, Uri>(),
            new List<string>(),
            new List<string>(),
            "etag",
            new Octokit.RateLimit(1, 1, 1));

        public HttpStatusCode StatusCode { get; } = statusCode;

        public string ContentType { get; } = "application/json";
    }
}