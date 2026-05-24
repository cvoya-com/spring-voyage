// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

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
/// the #2380 GitHub-side adoption of the connector-runtime-context seam,
/// re-targeted onto ADR-0047 §6's binding-auth resolver.
/// </summary>
public class GitHubConnectorRuntimeContextContributorTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OwnerUnit = new("11111111-2222-3333-4444-555555555555");
    private static readonly Address Subject = new(Address.UnitScheme, OwnerUnit);
    private static readonly DateTimeOffset TokenExpiry = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ContributeAsync_AppInstallationBinding_EmitsExpectedEnvVars()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: 4242,
            Events: null,
            Reviewer: "reviewer-login");
        var binding = MakeBinding(config);
        var contributor = BuildContributor(AppResolver("ghs_minted-token-123", TokenExpiry));

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
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

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
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: null));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvReviewer);
    }

    [Fact]
    public async Task ContributeAsync_WhitespaceReviewer_OmitsReviewerEnvVar()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "   "));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvReviewer);
    }

    [Fact]
    public async Task ContributeAsync_WritesBindingJsonFile_WithoutTokenInBody()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var contributor = BuildContributor(AppResolver("ghs_secret", TokenExpiry));

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
        // Unqualified repo (no `/`) is rejected by TryParseRepo so the
        // contributor returns Empty. Pre-ADR-0047 row shape — kept under
        // test as a regression guard.
        var binding = MakeBinding(new UnitGitHubConfig("platform", 4242, Reviewer: null));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.ShouldBe(ConnectorRuntimeContextContribution.Empty);
    }

    [Fact]
    public async Task ContributeAsync_PatBinding_EmitsTokenWithoutInstallationOrExpiry()
    {
        // PAT branch: the resolver supplies the secret value, the
        // contributor emits it under SPRING_CONNECTOR_GITHUB_TOKEN, and
        // the installation-id / token-expires-at env-vars are omitted
        // because they would be meaningless without an App installation
        // behind the credential.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: null,
            PatSecretName: "binding/abc/github/pat",
            Reviewer: "rev");
        var binding = MakeBinding(config);
        var contributor = BuildContributor(PatResolver("ghp_personal-token"));

        var contribution = await contributor.ContributeAsync(
            new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
            TestContext.Current.CancellationToken);

        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvOwner, "acme");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvRepo, "platform");
        contribution.EnvironmentVariables.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvToken, "ghp_personal-token");
        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvInstallationId);
        contribution.EnvironmentVariables.ShouldNotContainKey(
            GitHubConnectorRuntimeContextContributor.EnvTokenExpiresAt);
        contribution.WellKnownAliasEnvironmentVariables.ShouldNotBeNull();
        contribution.WellKnownAliasEnvironmentVariables!.ShouldContainKeyAndValue(
            GitHubConnectorRuntimeContextContributor.EnvTokenWellKnownAlias, "ghp_personal-token");
    }

    [Fact]
    public async Task ContributeAsync_AuthMissing_PropagatesException()
    {
        // The resolver raised the use-time auth-missing signal; the
        // contributor MUST surface it unchanged so the dispatcher fails
        // the launch with a structured error rather than landing a
        // half-configured container with no GitHub credential.
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var resolver = ThrowingResolver(new GitHubBindingAuthMissingException("secret gone"));
        var contributor = BuildContributor(resolver);

        await Should.ThrowAsync<GitHubBindingAuthMissingException>(async () =>
            await contributor.ContributeAsync(
                new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContributeAsync_ResolverThrowsUnexpected_WrappedAsInvalidOperationException()
    {
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var resolver = ThrowingResolver(new HttpRequestException("github 500"));
        var contributor = BuildContributor(resolver);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await contributor.ContributeAsync(
                new ConnectorRuntimeContextRequest(Subject, OwnerUnit, binding, TenantId),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(OwnerUnit.ToString("N"));
    }

    [Fact]
    public void ConnectorTypeId_MatchesGitHubConnectorType()
    {
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));
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
        var binding = MakeBinding(new UnitGitHubConfig("acme/platform", 4242, Reviewer: "rev"));
        var contributor = BuildContributor(AppResolver("ghs_dual-presence-token", TokenExpiry));

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
        var binding = MakeBinding(new UnitGitHubConfig("cvoya-com/spring-voyage", 4242, Reviewer: "rev"));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldNotBeNull();
        fragment.ShouldContain("### GitHub binding — cvoya-com/spring-voyage");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_OWNER");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_REPO");
        fragment.ShouldContain("$SPRING_CONNECTOR_GITHUB_TOKEN");
        fragment.ShouldContain("$GITHUB_TOKEN");
        fragment.ShouldContain("gh issue list --repo");
        // #2704: the fragment must name the canonical tool-call alternative
        // and explicitly disclaim the HTTP-URL fetch shape the engineer
        // hallucinated.
        fragment.ShouldContain("github.get_installation_token");
        fragment.ShouldContain("no such");
        // Tokens never appear verbatim in the prompt fragment — only the
        // env-var names do.
        fragment.ShouldNotContain("ghs_");
    }

    [Fact]
    public async Task GetPromptHintsAsync_UnqualifiedRepo_ReturnsNull()
    {
        // ADR-0047 §11: unqualified repo (no `/`) is rejected by
        // TryParseRepo so the contributor returns null.
        var binding = MakeBinding(new UnitGitHubConfig("platform", 4242, Reviewer: null));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public async Task GetPromptHintsAsync_EmptyRepo_ReturnsNull()
    {
        var binding = MakeBinding(new UnitGitHubConfig("", 4242, Reviewer: null));
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

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
        var contributor = BuildContributor(AppResolver("t", TokenExpiry));

        var fragment = await contributor.GetPromptHintsAsync(
            Subject, OwnerUnit, binding, TestContext.Current.CancellationToken);

        fragment.ShouldBeNull();
    }

    [Fact]
    public void BuildPromptFragment_RendersTheCanonicalShape()
    {
        // Snapshot-style assertion: pin the static fragment shape so
        // future reflows are deliberate.
        var fragment = GitHubConnectorRuntimeContextContributor.BuildPromptFragment(
            "cvoya-com", "spring-voyage");

        const string expected =
            "### GitHub binding — cvoya-com/spring-voyage\n\n" +
            "Your container has GitHub credentials and repo identity injected as env-vars:\n\n" +
            "- $SPRING_CONNECTOR_GITHUB_OWNER       — repo owner (cvoya-com)\n" +
            "- $SPRING_CONNECTOR_GITHUB_REPO        — repo name (spring-voyage)\n" +
            "- $SPRING_CONNECTOR_GITHUB_REVIEWER    — operator's GitHub login for review requests / assignee fallback\n" +
            "- $SPRING_CONNECTOR_GITHUB_TOKEN       — outbound bearer token (also exposed as $GITHUB_TOKEN for gh / git compatibility)\n" +
            "- $SPRING_CONNECTOR_GITHUB_TOKEN_EXPIRES_AT — token expiry (UTC ISO; only set on the App-installation auth path)\n\n" +
            "Use `gh` and `git` against the bound repo:\n\n" +
            "  REPO=\"$SPRING_CONNECTOR_GITHUB_OWNER/$SPRING_CONNECTOR_GITHUB_REPO\"\n" +
            "  gh issue list --repo \"$REPO\" --milestone v0.1 --state open\n\n" +
            "`gh` and `git` will pick up $GITHUB_TOKEN automatically — no `gh auth login` needed.\n\n" +
            "If you need the token from a tool-call shape (e.g. a non-CLI HTTP client),\n" +
            "call the platform tool `github.get_installation_token` — it returns the same\n" +
            "value as $GITHUB_TOKEN plus the credential kind and expiry. **Do not** try to\n" +
            "fetch the token by constructing an HTTP URL against the platform: no such\n" +
            "endpoint exists, and the env-var / tool are the only two ways to get it.\n\n" +
            "When you receive a message whose payload `source` is `github`, the connector\n" +
            "translated an inbound webhook into that message. The envelope shape and the\n" +
            "canonical intent vocabulary are published by the tool\n" +
            "`github.describe_inbound_contract` — input-less, idempotent, stable across the\n" +
            "connector's lifetime. Call it once on the first github-source turn and switch\n" +
            "on the resulting `intent` rather than on the raw `action`.";

        // Normalise CRLF on Windows so the snapshot test is platform-agnostic.
        fragment.Replace("\r\n", "\n").ShouldBe(expected);
    }

    private static UnitConnectorBinding MakeBinding(UnitGitHubConfig config)
    {
        var element = JsonSerializer.SerializeToElement(
            config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, element);
    }

    private static GitHubConnectorRuntimeContextContributor BuildContributor(
        GitHubBindingAuthResolver resolver) =>
        new(resolver, NullLogger<GitHubConnectorRuntimeContextContributor>.Instance);

    private static GitHubBindingAuthResolver AppResolver(string token, DateTimeOffset expiresAt) =>
        new StubBindingAuthResolver(
            new GitHubAuthCredential(token, GitHubAuthCredentialKind.AppInstallation, expiresAt));

    private static GitHubBindingAuthResolver PatResolver(string token) =>
        new StubBindingAuthResolver(
            new GitHubAuthCredential(token, GitHubAuthCredentialKind.PersonalAccessToken, ExpiresAt: null));

    private static GitHubBindingAuthResolver ThrowingResolver(Exception ex) =>
        new StubBindingAuthResolver(ex);

    /// <summary>
    /// Test double for <see cref="GitHubBindingAuthResolver"/> that
    /// returns a canned <see cref="GitHubAuthCredential"/> without touching
    /// the App auth surface or the secret store. Mirrors the pattern the
    /// old test used to stub <c>GitHubAppAuth</c> before ADR-0047 §6
    /// moved the dispatch behind the resolver seam.
    /// </summary>
    private sealed class StubBindingAuthResolver : GitHubBindingAuthResolver
    {
        private readonly GitHubAuthCredential? _credential;
        private readonly Exception? _exception;

        public StubBindingAuthResolver(GitHubAuthCredential credential)
            : base(
                new StubAppAuth(),
                NSubstitute.Substitute.For<IInstallationTokenCache>(),
                NoOpScopeFactory.Instance,
                NullLogger<GitHubBindingAuthResolver>.Instance)
        {
            _credential = credential;
        }

        public StubBindingAuthResolver(Exception exception)
            : base(
                new StubAppAuth(),
                NSubstitute.Substitute.For<IInstallationTokenCache>(),
                NoOpScopeFactory.Instance,
                NullLogger<GitHubBindingAuthResolver>.Instance)
        {
            _exception = exception;
        }

        public override Task<GitHubAuthCredential> ResolveAsync(
            UnitGitHubConfig binding,
            CancellationToken cancellationToken = default)
        {
            if (_exception is not null)
            {
                throw _exception;
            }
            return Task.FromResult(_credential!);
        }
    }

    private sealed class StubAppAuth : GitHubAppAuth
    {
        public StubAppAuth()
            : base(BuildOptions(), NullLoggerFactory.Instance)
        {
        }

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

    private sealed class NoOpScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public static readonly NoOpScopeFactory Instance = new();

        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException(
                "StubBindingAuthResolver bypasses the scope factory.");
    }
}
