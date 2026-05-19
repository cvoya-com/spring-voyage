// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Auth;

using System.Security.Cryptography;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the ADR-0047 §6 single-step binding-auth dispatch —
/// <see cref="GitHubBindingAuthResolver"/>. Verifies the two production
/// branches (App-installation token mint, PAT secret resolution) and the
/// defensive assertion the binding-create gate is supposed to make
/// unreachable.
/// </summary>
public class GitHubBindingAuthResolverTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;

    [Fact]
    public async Task ResolveAsync_AppInstallationBinding_MintsTokenThroughCache()
    {
        // App branch: the resolver delegates to IInstallationTokenCache,
        // which in production coalesces concurrent mints. The credential
        // surface carries the minted token + its server-stamped expiry.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(50);
        var auth = new StubAppAuth("ghs_minted", expiresAt);
        var cache = Substitute.For<IInstallationTokenCache>();
        cache.GetOrMintAsync(
                42L,
                Arg.Any<Func<long, CancellationToken, Task<InstallationAccessToken>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mint = call.Arg<Func<long, CancellationToken, Task<InstallationAccessToken>>>();
                return mint(42L, CancellationToken.None);
            });

        var resolver = BuildResolver(auth, cache);
        var binding = new UnitGitHubConfig(Repo: "acme/platform", AppInstallationId: 42L);

        var credential = await resolver.ResolveAsync(
            binding, TestContext.Current.CancellationToken);

        credential.Kind.ShouldBe(GitHubAuthCredentialKind.AppInstallation);
        credential.Token.ShouldBe("ghs_minted");
        credential.ExpiresAt.ShouldBe(expiresAt);
    }

    [Fact]
    public async Task ResolveAsync_PatBinding_ResolvesSecretThroughTenantScope()
    {
        // PAT branch: the resolver reads through ISecretResolver under
        // SecretScope.Tenant + the ambient tenant id. ADR-0003's Unit →
        // Tenant fall-through is automatic at the resolver layer.
        var secretResolver = Substitute.For<ISecretResolver>();
        secretResolver
            .ResolveWithPathAsync(
                Arg.Is<SecretRef>(r =>
                    r.Scope == SecretScope.Tenant
                    && r.OwnerId == TenantId
                    && r.Name == "binding/abc/github/pat"),
                Arg.Any<CancellationToken>())
            .Returns(new SecretResolution(
                Value: "ghp_personal-access-token",
                Path: SecretResolvePath.Direct,
                EffectiveRef: new SecretRef(SecretScope.Tenant, TenantId, "binding/abc/github/pat")));

        var resolver = BuildResolverWithSecret(secretResolver);
        var binding = new UnitGitHubConfig(
            Repo: "acme/platform",
            PatSecretName: "binding/abc/github/pat");

        var credential = await resolver.ResolveAsync(
            binding, TestContext.Current.CancellationToken);

        credential.Kind.ShouldBe(GitHubAuthCredentialKind.PersonalAccessToken);
        credential.Token.ShouldBe("ghp_personal-access-token");
        credential.ExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_PatBindingMissingSecret_RaisesAuthMissing()
    {
        // Use-time signal that the binding's pinned PAT name does not
        // resolve to a stored secret. Stable code matches the create-time
        // problem-details vocabulary so the CLI / portal handle both the
        // same way.
        var secretResolver = Substitute.For<ISecretResolver>();
        secretResolver
            .ResolveWithPathAsync(
                Arg.Any<SecretRef>(),
                Arg.Any<CancellationToken>())
            .Returns(new SecretResolution(
                Value: null,
                Path: SecretResolvePath.NotFound,
                EffectiveRef: null));

        var resolver = BuildResolverWithSecret(secretResolver);
        var binding = new UnitGitHubConfig(
            Repo: "acme/platform",
            PatSecretName: "binding/abc/github/pat");

        var ex = await Should.ThrowAsync<GitHubBindingAuthMissingException>(async () =>
            await resolver.ResolveAsync(
                binding, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(GitHubBindingAuthMissingException.CodeValue);
        ex.Message.ShouldContain("binding/abc/github/pat");
    }

    [Fact]
    public async Task ResolveAsync_AppInstallationMintFails_RaisesAuthMissing()
    {
        // Use-time signal that the App credentials are no longer accepted
        // by GitHub (rotated, revoked, installation removed).
        var auth = new ThrowingAppAuth(new InvalidOperationException("upstream 401"));
        var cache = Substitute.For<IInstallationTokenCache>();
        cache.GetOrMintAsync(
                Arg.Any<long>(),
                Arg.Any<Func<long, CancellationToken, Task<InstallationAccessToken>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mint = call.Arg<Func<long, CancellationToken, Task<InstallationAccessToken>>>();
                return mint(42L, CancellationToken.None);
            });

        var resolver = BuildResolver(auth, cache);
        var binding = new UnitGitHubConfig(Repo: "acme/platform", AppInstallationId: 42L);

        var ex = await Should.ThrowAsync<GitHubBindingAuthMissingException>(async () =>
            await resolver.ResolveAsync(
                binding, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(GitHubBindingAuthMissingException.CodeValue);
        ex.InnerException.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_NeitherFieldSet_DefensiveAssertionRaisesAuthMissing()
    {
        // The binding-create gate (ADR-0047 §11) is supposed to make this
        // unreachable, but the resolver defends in depth — a wiring bug
        // that bypasses the gate surfaces as the same use-time code the
        // operator already understands, never as a NullReferenceException.
        var resolver = BuildResolver(
            new StubAppAuth("unused", DateTimeOffset.UtcNow),
            Substitute.For<IInstallationTokenCache>());
        var binding = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: null,
            PatSecretName: null);

        var ex = await Should.ThrowAsync<GitHubBindingAuthMissingException>(async () =>
            await resolver.ResolveAsync(
                binding, TestContext.Current.CancellationToken));

        ex.Code.ShouldBe(GitHubBindingAuthMissingException.CodeValue);
    }

    [Fact]
    public async Task ResolveAsync_BothFieldsSet_PrefersAppInstallationBranch()
    {
        // The binding-create gate rejects "both set" so this combination
        // should never reach the resolver in practice. If it does — for
        // example through a hand-crafted JSON insert — the resolver picks
        // the App branch because that is what ADR-0034's pre-existing
        // OSS-package shape pins. The defensive read prevents a runtime
        // null-deref; the operator still sees an authentic credential.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(50);
        var auth = new StubAppAuth("ghs_minted", expiresAt);
        var cache = Substitute.For<IInstallationTokenCache>();
        cache.GetOrMintAsync(
                42L,
                Arg.Any<Func<long, CancellationToken, Task<InstallationAccessToken>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var mint = call.Arg<Func<long, CancellationToken, Task<InstallationAccessToken>>>();
                return mint(42L, CancellationToken.None);
            });
        var resolver = BuildResolver(auth, cache);
        var binding = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: 42L,
            PatSecretName: "binding/abc/github/pat");

        var credential = await resolver.ResolveAsync(
            binding, TestContext.Current.CancellationToken);

        credential.Kind.ShouldBe(GitHubAuthCredentialKind.AppInstallation);
    }

    private static GitHubBindingAuthResolver BuildResolver(
        GitHubAppAuth auth, IInstallationTokenCache cache)
    {
        // PAT-branch wiring is inert here (no tenant context / no secret
        // resolver wired) because the App branch never touches the PAT
        // resolution path. Using a real ServiceCollection keeps the scope
        // factory honest — singleton resolver, scoped services materialised
        // per call.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(TenantId));
        services.AddScoped<ISecretResolver>(_ =>
            throw new InvalidOperationException("PAT branch should not be reached on this path."));
        var provider = services.BuildServiceProvider();
        return new GitHubBindingAuthResolver(
            auth, cache, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GitHubBindingAuthResolver>.Instance);
    }

    private static GitHubBindingAuthResolver BuildResolverWithSecret(ISecretResolver secretResolver)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(TenantId));
        services.AddScoped(_ => secretResolver);
        var provider = services.BuildServiceProvider();
        return new GitHubBindingAuthResolver(
            new StubAppAuth("unused", DateTimeOffset.UtcNow),
            Substitute.For<IInstallationTokenCache>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<GitHubBindingAuthResolver>.Instance);
    }

    private sealed class StubAppAuth : GitHubAppAuth
    {
        private readonly string _token;
        private readonly DateTimeOffset _expiresAt;

        public StubAppAuth(string token, DateTimeOffset expiresAt)
            : base(BuildOptions(), NullLoggerFactory.Instance)
        {
            _token = token;
            _expiresAt = expiresAt;
        }

        public override Task<InstallationAccessToken> MintInstallationTokenAsync(
            long installationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new InstallationAccessToken(_token, _expiresAt));
    }

    private sealed class ThrowingAppAuth : GitHubAppAuth
    {
        private readonly Exception _exception;

        public ThrowingAppAuth(Exception exception)
            : base(BuildOptions(), NullLoggerFactory.Instance)
        {
            _exception = exception;
        }

        public override Task<InstallationAccessToken> MintInstallationTokenAsync(
            long installationId,
            CancellationToken cancellationToken = default)
            => throw _exception;
    }

    private static GitHubConnectorOptions BuildOptions()
    {
        using var rsa = RSA.Create(2048);
        return new GitHubConnectorOptions
        {
            AppId = 1,
            PrivateKeyPem = rsa.ExportRSAPrivateKeyPem(),
        };
    }

    private sealed class StubTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid CurrentTenantId { get; } = tenantId;
    }
}
