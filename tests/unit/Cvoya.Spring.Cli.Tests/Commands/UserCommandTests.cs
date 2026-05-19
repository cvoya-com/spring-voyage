// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;
using System.Net.Http;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Core.Tenancy;

using Shouldly;

using Xunit;

/// <summary>
/// Parser + wire-level tests for the <c>spring user</c> verb tree
/// (ADR-0047 §§ 1–2, 12). Replaces the deleted <c>HumanCommandTests</c>
/// identity coverage; the verb shape is intentionally identical (one
/// row per <c>(tenant_user, connector)</c> pair) so the rename is the
/// only operator-visible change.
/// </summary>
public class UserCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    private static Option<string> CreateOutputOption()
        => new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };

    // ── Parse-time coverage ─────────────────────────────────────────────

    [Theory]
    [InlineData("user identity set --connector github --username octocat")]
    [InlineData("user identity set --connector github --username octocat --display-handle Octo-Cat")]
    [InlineData("user identity list")]
    [InlineData("user identity remove --connector github --username octocat")]
    [InlineData("user identity authorize-github")]
    [InlineData("user identity authorize-github --no-browser")]
    public void UserVerbs_Parse(string argLine)
    {
        var outputOption = CreateOutputOption();
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(UserCommand.Create(outputOption));

        var parseResult = rootCommand.Parse(argLine);

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void UserIdentitySet_RequiresConnectorAndUsername()
    {
        var outputOption = CreateOutputOption();
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(UserCommand.Create(outputOption));

        rootCommand.Parse("user identity set").Errors.ShouldNotBeEmpty();
        rootCommand.Parse("user identity set --connector github").Errors.ShouldNotBeEmpty();
        rootCommand.Parse("user identity set --username octocat").Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UserIdentityRemove_RequiresConnectorAndUsername()
    {
        var outputOption = CreateOutputOption();
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(UserCommand.Create(outputOption));

        rootCommand.Parse("user identity remove").Errors.ShouldNotBeEmpty();
    }

    // ── --user default + parse ──────────────────────────────────────────

    [Fact]
    public void TryResolveTenantUserId_OmittedFlag_DefaultsToOssOperator()
    {
        // ADR-0047 §§ 1, 3: OSS deployments ship with exactly one
        // tenant user, pinned to OssTenantUserIds.Operator. The CLI
        // defaults --user to that UUID so the operator does not have
        // to look it up.
        UserCommand.TryResolveTenantUserId(null, out var id, out var error).ShouldBeTrue();
        id.ShouldBe(OssTenantUserIds.Operator);
        error.ShouldBeEmpty();
    }

    [Fact]
    public void TryResolveTenantUserId_BlankFlag_DefaultsToOssOperator()
    {
        UserCommand.TryResolveTenantUserId("   ", out var id, out var error).ShouldBeTrue();
        id.ShouldBe(OssTenantUserIds.Operator);
        error.ShouldBeEmpty();
    }

    [Fact]
    public void TryResolveTenantUserId_ExplicitDashedGuid_Parses()
    {
        UserCommand.TryResolveTenantUserId(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", out var id, out var error)
            .ShouldBeTrue();
        id.ShouldBe(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        error.ShouldBeEmpty();
    }

    [Fact]
    public void TryResolveTenantUserId_GarbageInput_ReturnsHint()
    {
        UserCommand.TryResolveTenantUserId("not-a-guid", out _, out var error).ShouldBeFalse();
        error.ShouldContain("not a valid TenantUser UUID");
    }

    [Fact]
    public void TryResolveTenantUserId_GuidEmpty_Rejected()
    {
        UserCommand.TryResolveTenantUserId(Guid.Empty.ToString(), out _, out var error)
            .ShouldBeFalse();
        error.ShouldContain("not a valid TenantUser UUID");
    }

    // ── Wire-level wrappers ─────────────────────────────────────────────

    [Fact]
    public async Task UpsertTenantUserConnectorIdentityAsync_PostsRequestBody()
    {
        // The endpoint lives at POST /api/v1/tenant/users/{id}/identities
        // and the CLI wrapper round-trips the (connectorId, username,
        // displayHandle) tuple unchanged so the operator-supplied flags
        // land on the wire in the documented shape.
        var tenantUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/users/{tenantUserId:D}/identities",
            expectedMethod: HttpMethod.Post,
            responseBody:
                $$"""{"tenantUserId":"{{tenantUserId}}","connectorId":"github","username":"octocat","displayHandle":"Octo Cat","createdAt":"2026-05-18T00:00:00Z","updatedAt":"2026-05-18T00:00:00Z"}""",
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var response = await client.UpsertTenantUserConnectorIdentityAsync(
            tenantUserId,
            "github",
            "octocat",
            "Octo Cat",
            TestContext.Current.CancellationToken);

        response.Username.ShouldBe("octocat");
        response.DisplayHandle.ShouldBe("Octo Cat");
        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();
        capturedBody.ShouldContain("\"connectorId\":\"github\"");
        capturedBody.ShouldContain("\"username\":\"octocat\"");
        capturedBody.ShouldContain("Octo Cat");
    }

    [Fact]
    public async Task ListTenantUserConnectorIdentitiesAsync_HitsIdentitiesEndpoint()
    {
        var tenantUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/users/{tenantUserId:D}/identities",
            expectedMethod: HttpMethod.Get,
            responseBody:
                $$"""[{"tenantUserId":"{{tenantUserId}}","connectorId":"github","username":"octocat","displayHandle":null,"createdAt":"2026-05-18T00:00:00Z","updatedAt":"2026-05-18T00:00:00Z"}]""");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var rows = await client.ListTenantUserConnectorIdentitiesAsync(
            tenantUserId, TestContext.Current.CancellationToken);

        rows.Count.ShouldBe(1);
        rows[0].ConnectorId.ShouldBe("github");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RemoveTenantUserConnectorIdentityAsync_HitsDeleteEndpoint()
    {
        // The DELETE wire shape carries (connectorId, username) as query
        // params; the wrapper round-trips them through the Kiota query
        // builder so the endpoint's idempotent path runs even when the
        // row is already absent.
        var tenantUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        string? capturedQuery = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/users/{tenantUserId:D}/identities",
            expectedMethod: HttpMethod.Delete,
            responseBody: string.Empty,
            returnStatusCode: HttpStatusCode.NoContent,
            validateQuery: q => capturedQuery = q);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        await client.RemoveTenantUserConnectorIdentityAsync(
            tenantUserId, "github", "octocat",
            TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("connectorId=github");
        capturedQuery.ShouldContain("username=octocat");
    }

    [Fact]
    public async Task BeginGitHubOAuthAuthorizationAsync_SendsIntentAndIds()
    {
        // ADR-0047 §13: the authorize request body carries the new
        // intent / tenantUserId / bindingId fields; the wrapper rounds
        // them through the Kiota POST so the server-side
        // GitHubOAuthEndpoints.AuthorizeAsync sees the typed payload.
        var tenantUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/oauth/authorize",
            expectedMethod: HttpMethod.Post,
            responseBody:
                """{"authorizeUrl":"https://github.com/login/oauth/authorize?state=abc","state":"abc"}""",
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var response = await client.BeginGitHubOAuthAuthorizationAsync(
            intent: "user-identity",
            tenantUserId: tenantUserId,
            bindingId: null,
            scopes: new[] { "repo", "read:user" },
            clientState: null,
            ct: TestContext.Current.CancellationToken);

        response.State.ShouldBe("abc");
        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();
        capturedBody.ShouldContain("\"intent\":\"user-identity\"");
        capturedBody.ShouldContain(tenantUserId.ToString());
        capturedBody.ShouldContain("\"repo\"");
        capturedBody.ShouldContain("\"read:user\"");
    }

    [Fact]
    public async Task GetGitHubOAuthSessionAsync_SurfacesPatSecretName()
    {
        // ADR-0047 §13: the OAuth session response carries the persisted
        // secret name so the CLI / portal can surface it post-callback
        // (the operator's terminal prints "spring connector bind
        // --pat-secret-name <name>"). The wire field is optional —
        // when the persister returned Skipped the field stays null.
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/oauth/session/sess-1",
            expectedMethod: HttpMethod.Get,
            responseBody:
                """{"sessionId":"sess-1","login":"octocat","userId":42,"scopes":"repo","expiresAt":null,"createdAt":"2026-05-18T00:00:00Z","clientState":null,"patSecretName":"binding/abc/github/pat","bindingId":"abcdef1234"}""");
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var session = await client.GetGitHubOAuthSessionAsync(
            "sess-1", TestContext.Current.CancellationToken);

        session.ShouldNotBeNull();
        session!.Login.ShouldBe("octocat");
        session.PatSecretName.ShouldBe("binding/abc/github/pat");
        session.BindingId.ShouldBe("abcdef1234");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetGitHubOAuthSessionAsync_NullOn404()
    {
        var handler = new MockHttpMessageHandler(
            expectedPath: "/api/v1/tenant/connectors/github/oauth/session/missing",
            expectedMethod: HttpMethod.Get,
            responseBody: """{"title":"Not Found","status":404}""",
            returnStatusCode: HttpStatusCode.NotFound);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        var session = await client.GetGitHubOAuthSessionAsync(
            "missing", TestContext.Current.CancellationToken);

        session.ShouldBeNull();
    }
}
