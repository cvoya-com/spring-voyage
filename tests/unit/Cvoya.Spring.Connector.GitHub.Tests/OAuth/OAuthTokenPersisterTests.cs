// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using System.Collections.Concurrent;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="OAuthTokenPersister"/> per ADR-0047 §13. Drives
/// each initiation-intent branch and asserts the binding-scoped secret
/// name from ADR-0047 §5 is produced under the right scope.
/// </summary>
public class OAuthTokenPersisterTests
{
    private const string AccessToken = "ghu_test_token";
    private static readonly Guid TestTenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid TestTenantUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly GitHubUserIdentity TestIdentity =
        new("octocat", 42, "Octo Cat", "octo@example.com");

    [Fact]
    public async Task PersistAsync_Unspecified_SkipsSideEffects()
    {
        // ADR-0047 §13 legacy path: the persister does not write a tenant
        // secret and does not touch the identity row when the intent is
        // Unspecified — the OAuth scaffolding's session-only behaviour is
        // preserved for the list-repositories-only flow.
        var harness = new Harness();
        var persister = harness.Build();

        var outcome = await persister.PersistAsync(
            AccessToken,
            TestIdentity,
            new OAuthInitiationContext(
                OAuthInitiationIntent.Unspecified, null, null),
            TestContext.Current.CancellationToken);

        outcome.Outcome.ShouldBe(OAuthTokenPersistKind.Skipped);
        outcome.PatSecretName.ShouldBeNull();
        outcome.BindingId.ShouldBeNull();
        outcome.IdentityOutcome.ShouldBeNull();
        harness.SecretStore.WriteCount.ShouldBe(0);
        await harness.Registry.DidNotReceiveWithAnyArgs().RegisterAsync(
            default!, default!, default, TestContext.Current.CancellationToken);
        await harness.IdentityWriter.DidNotReceiveWithAnyArgs().UpsertAsync(
            default, default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PersistAsync_NullInitiation_SkipsSideEffects()
    {
        // A null initiation context falls through as Unspecified — the
        // same legacy behaviour as the explicit Unspecified branch.
        var harness = new Harness();
        var persister = harness.Build();

        var outcome = await persister.PersistAsync(
            AccessToken,
            TestIdentity,
            initiation: null,
            TestContext.Current.CancellationToken);

        outcome.Outcome.ShouldBe(OAuthTokenPersistKind.Skipped);
        harness.SecretStore.WriteCount.ShouldBe(0);
    }

    [Fact]
    public async Task PersistAsync_BindingWizard_UsesSuppliedBindingId()
    {
        // ADR-0047 §13 option (a): the wizard pre-mints the binding UUID
        // and supplies it through the initiation context. The persister
        // writes the secret under exactly that id so the subsequent
        // binding-create call references the same address without
        // rewrites.
        var bindingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var harness = new Harness();
        var persister = harness.Build();

        var outcome = await persister.PersistAsync(
            AccessToken,
            TestIdentity,
            new OAuthInitiationContext(
                OAuthInitiationIntent.BindingWizard,
                TenantUserId: null,
                BindingId: bindingId),
            TestContext.Current.CancellationToken);

        outcome.Outcome.ShouldBe(OAuthTokenPersistKind.Persisted);
        outcome.BindingId.ShouldBe(bindingId);
        outcome.PatSecretName.ShouldBe(
            OAuthTokenPersister.BuildBindingSecretName(bindingId));
        outcome.IdentityOutcome.ShouldBeNull();
        harness.SecretStore.WriteCount.ShouldBe(1);
        harness.SecretStore.LastWrite.ShouldBe(AccessToken);

        // Registry got the (Tenant, current-tenant, secret-name) triple.
        await harness.Registry.Received(1).RegisterAsync(
            Arg.Is<SecretRef>(r =>
                r.Scope == SecretScope.Tenant
                && r.OwnerId == TestTenantId
                && r.Name == outcome.PatSecretName),
            Arg.Any<string>(),
            SecretOrigin.PlatformOwned,
            Arg.Any<CancellationToken>());

        // The wizard branch does not refresh the calling identity — that
        // is the user-identity-surface intent's job.
        await harness.IdentityWriter.DidNotReceiveWithAnyArgs().UpsertAsync(
            default, default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PersistAsync_UserIdentitySurface_RefreshesIdentity()
    {
        // ADR-0047 §13 (2): the user-identity intent both persists the
        // token and refreshes the calling TenantUser's GitHub display
        // identity. The persister mints a transient binding UUID so the
        // §5 secret-name shape stays uniform; the writer call carries
        // the OAuth user-info login + name.
        var harness = new Harness();
        harness.IdentityWriter
            .UpsertAsync(
                TestTenantUserId, "github", TestIdentity.Login,
                TestIdentity.Name, Arg.Any<CancellationToken>())
            .Returns(TenantUserConnectorIdentityUpsertOutcome.Upserted);
        var persister = harness.Build();

        var outcome = await persister.PersistAsync(
            AccessToken,
            TestIdentity,
            new OAuthInitiationContext(
                OAuthInitiationIntent.UserIdentitySurface,
                TenantUserId: TestTenantUserId,
                BindingId: null),
            TestContext.Current.CancellationToken);

        outcome.Outcome.ShouldBe(OAuthTokenPersistKind.Persisted);
        outcome.PatSecretName.ShouldNotBeNullOrWhiteSpace();
        outcome.PatSecretName!.ShouldStartWith("binding/");
        outcome.PatSecretName.ShouldEndWith("/github/pat");
        outcome.BindingId.ShouldNotBeNull();
        outcome.IdentityOutcome.ShouldBe(
            TenantUserConnectorIdentityUpsertOutcome.Upserted);
        harness.SecretStore.WriteCount.ShouldBe(1);

        await harness.IdentityWriter.Received(1).UpsertAsync(
            TestTenantUserId,
            "github",
            TestIdentity.Login,
            TestIdentity.Name,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PersistAsync_UserIdentitySurface_NoTenantUserId_SkipsIdentityRefresh()
    {
        // Defensive branch — when the calling tenant user is unknown
        // (e.g. the CLI omitted the id but the endpoint did not fall
        // through to the OSS-operator default), the persister still
        // writes the secret but does not touch any identity row.
        var harness = new Harness();
        var persister = harness.Build();

        var outcome = await persister.PersistAsync(
            AccessToken,
            TestIdentity,
            new OAuthInitiationContext(
                OAuthInitiationIntent.UserIdentitySurface,
                TenantUserId: null,
                BindingId: null),
            TestContext.Current.CancellationToken);

        outcome.Outcome.ShouldBe(OAuthTokenPersistKind.Persisted);
        outcome.PatSecretName.ShouldNotBeNullOrWhiteSpace();
        outcome.IdentityOutcome.ShouldBeNull();
        await harness.IdentityWriter.DidNotReceiveWithAnyArgs().UpsertAsync(
            default, default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BuildBindingSecretName_FollowsAdr0047Section5()
    {
        // §5: binding/<binding-id-no-dash>/<connector-slug>/pat. The
        // no-dash hex form is the wire form every public surface uses,
        // so the secret name shape stays uniform with audit logs and
        // dashboards.
        var bindingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        OAuthTokenPersister.BuildBindingSecretName(bindingId)
            .ShouldBe($"binding/{bindingId:N}/github/pat");
    }

    private sealed class Harness
    {
        public RecordingSecretStore SecretStore { get; } = new();
        public ISecretRegistry Registry { get; } = Substitute.For<ISecretRegistry>();
        public ITenantUserConnectorIdentityWriter IdentityWriter { get; } =
            Substitute.For<ITenantUserConnectorIdentityWriter>();
        public ITenantContext TenantContext { get; } = new FixedTenantContext(TestTenantId);

        public OAuthTokenPersister Build()
        {
            // The persister captures IServiceScopeFactory and resolves
            // its scoped dependencies on every call; build a tiny
            // service provider so the production wiring is exercised.
            var services = new ServiceCollection();
            services.AddSingleton(TenantContext);
            services.AddSingleton<ISecretStore>(SecretStore);
            services.AddSingleton(Registry);
            services.AddSingleton(IdentityWriter);
            var sp = services.BuildServiceProvider();
            return new OAuthTokenPersister(
                sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OAuthTokenPersister>.Instance);
        }
    }

    private sealed class FixedTenantContext(Guid tenantId) : ITenantContext
    {
        public Guid CurrentTenantId { get; } = tenantId;
    }

    /// <summary>
    /// Recording in-memory <see cref="ISecretStore"/> for assertions on
    /// what the persister wrote. The real store is opaque to callers; we
    /// only need to verify the call happened with the expected
    /// plaintext.
    /// </summary>
    private sealed class RecordingSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public int WriteCount { get; private set; }
        public string? LastWrite { get; private set; }

        public Task<string> WriteAsync(string plaintext, CancellationToken ct)
        {
            WriteCount++;
            LastWrite = plaintext;
            var key = Guid.NewGuid().ToString("N");
            _values[key] = plaintext;
            return Task.FromResult(key);
        }

        public Task<string?> ReadAsync(string storeKey, CancellationToken ct)
        {
            _values.TryGetValue(storeKey, out var value);
            return Task.FromResult<string?>(value);
        }

        public Task DeleteAsync(string storeKey, CancellationToken ct)
        {
            _values.TryRemove(storeKey, out _);
            return Task.CompletedTask;
        }
    }
}
