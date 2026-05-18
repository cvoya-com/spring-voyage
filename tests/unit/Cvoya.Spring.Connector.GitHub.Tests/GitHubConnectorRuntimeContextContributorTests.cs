// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Security.Cryptography;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="GitHubConnectorRuntimeContextContributor"/> —
/// the #2380 GitHub-side adoption of the connector-runtime-context seam.
/// </summary>
public class GitHubConnectorRuntimeContextContributorTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OwnerUnit = new("11111111-2222-3333-4444-555555555555");
    private static readonly Address Subject = new(Address.UnitScheme, OwnerUnit);
    private static readonly DateTimeOffset TokenExpiry = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContributeAsync_ValidBinding_EmitsExpectedEnvVars()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: 4242,
            Events: null,
            Reviewer: "reviewer-login");
        var binding = MakeBinding(config);
        var auth = new FakeGitHubAppAuth("ghs_minted-token-123", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvOwner, "acme");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvRepo, "platform");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvInstallationId, "4242");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvReviewer, "reviewer-login");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvToken, "ghs_minted-token-123");
        contribution.EnvironmentVariables.ShouldContainKey(
            GitHubConnectorRuntimeContextContributor.EnvTokenExpiresAt);
    }

    [Fact]
    public async Task ContributeAsync_EnvVarsRespectSeamNamespace()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: "rev"));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        foreach (var key in contribution.EnvironmentVariables.Keys)
        {
            key.ShouldStartWith("SPRING_CONNECTOR_GITHUB_");
        }
    }

    [Fact]
    public async Task ContributeAsync_NoReviewer_OmitsReviewerEnvVar()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: null));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvReviewer);
    }

    [Fact]
    public async Task ContributeAsync_WhitespaceReviewer_OmitsReviewerEnvVar()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: "   "));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvReviewer);
    }

    [Fact]
    public async Task ContributeAsync_WritesBindingJsonFile_WithoutTokenInBody()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: "rev"));
        var auth = new FakeGitHubAppAuth("ghs_secret", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.ContextFiles.ShouldContainKey(
            GitHubConnectorRuntimeContextContributor.BindingFilePath);
        var bindingJson = contribution.ContextFiles[GitHubConnectorRuntimeContextContributor.BindingFilePath];
        bindingJson.ShouldNotContain("ghs_secret"); // never leak token into the JSON
        using var doc = JsonDocument.Parse(bindingJson);
        doc.RootElement.GetProperty("owner").GetString().ShouldBe("acme");
        doc.RootElement.GetProperty("repo").GetString().ShouldBe("platform");
        doc.RootElement.GetProperty("installationId").GetInt64().ShouldBe(4242);
        doc.RootElement.GetProperty("reviewer").GetString().ShouldBe("rev");
    }

    [Fact]
    public async Task ContributeAsync_MissingOwner_ReturnsEmpty()
    {
        var binding = MakeBinding(new UnitGitHubConfig("", "platform", 4242, Reviewer: null));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.ShouldBe(ConnectorRuntimeContextContribution.Empty);
    }

    [Fact]
    public async Task ContributeAsync_MissingInstallationId_ReturnsEmpty()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", AppInstallationId: null));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.ShouldBe(ConnectorRuntimeContextContribution.Empty);
    }

    [Fact]
    public async Task ContributeAsync_TokenMintFails_PropagatesException()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: "rev"));
        var auth = new ThrowingGitHubAppAuth(new HttpRequestException("github 500"));
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await contributor.ContributeAsync(
                new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("4242");
    }

    [Fact]
    public void ConnectorTypeId_MatchesGitHubConnectorType()
    {
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        contributor.ConnectorTypeId.ShouldBe(GitHubConnectorType.GitHubTypeId);
    }

    /// <summary>
    /// #2442: GITHUB_TOKEN is published alongside the namespaced var
    /// with the same value so <c>gh</c> / <c>git</c> pick it up
    /// natively. The namespaced var stays canonical; the alias is the
    /// downstream-CLI convenience hop.
    /// </summary>
    [Fact]
    public async Task ContributeAsync_PublishesGithubTokenAliasWithSameValue()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "platform", 4242, Reviewer: "rev"));
        var auth = new FakeGitHubAppAuth("ghs_dual-presence-token", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvToken, "ghs_dual-presence-token");
        contribution.WellKnownAliasEnvironmentVariables.ShouldNotBeNull();
        contribution.WellKnownAliasEnvironmentVariables!.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvTokenWellKnownAlias, "ghs_dual-presence-token");
        // GITHUB_TOKEN sits intentionally outside the namespaced bucket
        // (the namespace check would otherwise reject it).
        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvTokenWellKnownAlias);
    }

    [Fact]
    public async Task GetPromptHintsAsync_ValidBinding_ReturnsFragmentNamingTheRepo()
    {
        var binding = MakeBinding(new UnitGitHubConfig("cvoya-com", "spring-voyage", 4242, Reviewer: "rev"));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain("### GitHub binding — cvoya-com/spring-voyage");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_OWNER");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_REPO");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_TOKEN");
        fragment.ShouldContain("$GITHUB_TOKEN");
        fragment.ShouldContain("gh issue list --repo");
        // Tokens never appear verbatim in the prompt fragment — only the
        // env-var names do.
        fragment.ShouldNotContain("ghs_");
    }

    [Fact]
    public async Task GetPromptHintsAsync_MissingOwner_ReturnsNull()
    {
        var binding = MakeBinding(new UnitGitHubConfig("", "platform", 4242, Reviewer: null));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptHintsAsync_MissingRepo_ReturnsNull()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme", "", 4242, Reviewer: null));
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptHintsAsync_MalformedJson_ReturnsNull()
    {
        // A binding whose Config does not deserialise to UnitGitHubConfig —
        // e.g. a primitive instead of an object — must be tolerated so a
        // single bad binding does not poison the whole launch's prompt
        // assembly.
        var malformed = JsonSerializer.SerializeToElement("not-an-object");
        var binding = new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, malformed);
        var auth = new FakeGitHubAppAuth("t", TokenExpiry);
        var contributor = new GitHubConnectorRuntimeContextContributor(
            auth, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public void BuildPromptFragment_RendersTheCanonicalShape()
    {
        // Snapshot-style assertion: pin the static fragment shape so future
        // reflows are deliberate. Mirrors the exact text in issue #2442's
        // body.
        var fragment = GitHubConnectorRuntimeContextContributor.BuildPromptFragment(
            "cvoya-com", "spring-voyage");

        const string expected =
            "### GitHub binding — cvoya-com/spring-voyage\n\n" +
            "Your container has GitHub credentials and repo identity injected as env-vars:\n\n" +
            "- $SPRING_CONNECTOR_GITHUB_OWNER       — repo owner (cvoya-com)\n" +
            "- $SPRING_CONNECTOR_GITHUB_REPO        — repo name (spring-voyage)\n" +
            "- $SPRING_CONNECTOR_GITHUB_REVIEWER    — operator's GitHub login for review requests / assignee fallback\n" +
            "- $SPRING_CONNECTOR_GITHUB_TOKEN       — short-lived installation token (also exposed as $GITHUB_TOKEN for gh / git compatibility)\n" +
            "- $SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT — token expiry (UTC ISO)\n\n" +
            "Use `gh` and `git` against the bound repo:\n\n" +
            "  REPO=\"$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO\"\n" +
            "  gh issue list --repo \"$REPO\" --milestone v0.1 --state open\n\n" +
            "`gh` and `git` will pick up $GITHUB_TOKEN automatically — no `gh auth login` needed.";

        // Normalise CRLF on Windows so the snapshot test is platform-agnostic.
        fragment.Replace("\r\n", "\n").ShouldBe(expected);
    }

    private static UnitConnectorBinding MakeBinding(UnitGitHubConfig config)
    {
        var element = JsonSerializer.SerializeToElement(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, element);
    }

    /// <summary>
    /// Test double for <see cref="GitHubAppAuth"/> that returns a canned
    /// installation access token without performing JWT generation or HTTP.
    /// </summary>
    private sealed class FakeGitHubAppAuth(string token, DateTimeOffset expiresAt)
        : GitHubAppAuth(BuildOptions(), NullLoggerFactory.Instance)
    {
        public override Task<InstallationAccessToken> MintInstallationTokenAsync(
            long installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallationAccessToken(token, expiresAt));
    }

    private sealed class ThrowingGitHubAppAuth(Exception ex)
        : GitHubAppAuth(BuildOptions(), NullLoggerFactory.Instance)
    {
        public override Task<InstallationAccessToken> MintInstallationTokenAsync(
            long installationId,
            CancellationToken cancellationToken = default)
            => throw ex;
    }

    private static GitHubConnectorOptions BuildOptions()
    {
        // Generate a real RSA key per call. The fake / throwing auth subclasses
        // override MintInstallationTokenAsync and never call GenerateJwt(), so
        // this key is unused except to satisfy GitHubAppAuth's constructor
        // invariants. Mirrors the existing GitHubAppAuthTests pattern.
        using var rsa = RSA.Create(2048);
        return new GitHubConnectorOptions
        {
            AppId = 1,
            PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(),
        };
    }
}
