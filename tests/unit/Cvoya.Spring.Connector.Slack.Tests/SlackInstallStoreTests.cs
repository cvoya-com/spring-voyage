// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack;
using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for <see cref="SlackInstallStore"/>. Exercises
/// the EF-backed persistence path against an in-memory SpringDbContext
/// and a fake secret store / registry — the unit-level integration
/// proves the install / disconnect contracts the OAuth service relies
/// on (ADR-0061 §2.3 / §2.5 / §7.5).
/// </summary>
public class SlackInstallStoreTests
{
    private static readonly Guid TestTenantId = new("aaaaaaaa-bbbb-cccc-dddd-000000000001");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PersistInstallAsync_WritesBindingAndWorkspaceMapAndSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var payload = new SlackInstallPayload(
            TeamId: "T-install",
            TeamName: "Test Workspace",
            BotUserId: "U-bot",
            BotAccessToken: "xoxb-test",
            SigningSecret: "signing-test",
            InstallerUserId: "U-installer",
            EnterpriseId: null);

        await harness.Store.PersistInstallAsync(payload, ct);

        // Binding row landed.
        var binding = await harness.BindingStore.GetAsync("slack", ct);
        binding.ShouldNotBeNull();
        binding!.TypeId.ShouldBe(SlackConnectorType.SlackTypeId);
        var config = binding.Config.Deserialize<TenantSlackConfig>(JsonOptions);
        config.ShouldNotBeNull();
        config!.TeamId.ShouldBe("T-install");
        config.BotUserId.ShouldBe("U-bot");
        config.InstallerUserId.ShouldBe("U-installer");
        config.SingleUserMode.ShouldBeTrue();
        config.Mode.ShouldBe(SlackBindingMode.Workspace);
        config.BoundUsers.Count.ShouldBe(1);
        config.BoundUsers[0].SlackUserId.ShouldBe("U-installer");
        config.BoundUsers[0].TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        // Workspace map row landed.
        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var mapRow = await db.TenantSlackWorkspaceMap
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.TeamId == "T-install", ct);
            mapRow.ShouldNotBeNull();
            mapRow!.TenantId.ShouldBe(TestTenantId);
            mapRow.TeamName.ShouldBe("Test Workspace");
        }

        // Secrets persisted under expected names.
        harness.FakeSecretStore.Stored.ShouldContainKey(config.BotTokenSecretName);
        harness.FakeSecretStore.Stored[config.BotTokenSecretName].ShouldBe("xoxb-test");
        harness.FakeSecretStore.Stored.ShouldContainKey(config.SigningSecretSecretName);
        harness.FakeSecretStore.Stored[config.SigningSecretSecretName].ShouldBe("signing-test");
    }

    [Fact]
    public async Task GetExistingBindingAsync_ReturnsSnapshotAfterPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var payload = new SlackInstallPayload(
            TeamId: "T-snap",
            TeamName: null,
            BotUserId: "U-bot",
            BotAccessToken: "xoxb",
            SigningSecret: "sig",
            InstallerUserId: "U-installer",
            EnterpriseId: null);
        await harness.Store.PersistInstallAsync(payload, ct);

        var snapshot = await harness.Store.GetExistingBindingAsync(ct);
        snapshot.ShouldNotBeNull();
        snapshot!.TeamId.ShouldBe("T-snap");
        snapshot.BotTokenSecretName.ShouldContain("T-snap");
        snapshot.SigningSecretSecretName.ShouldContain("T-snap");
    }

    [Fact]
    public async Task GetExistingBindingAsync_NoRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var snapshot = await harness.Store.GetExistingBindingAsync(ct);
        snapshot.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteInstallAsync_RemovesBindingAndWorkspaceMapAndSecrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var payload = new SlackInstallPayload(
            TeamId: "T-delete",
            TeamName: null,
            BotUserId: "U-bot",
            BotAccessToken: "xoxb",
            SigningSecret: "sig",
            InstallerUserId: "U-installer",
            EnterpriseId: null);
        await harness.Store.PersistInstallAsync(payload, ct);

        var snapshot = await harness.Store.GetExistingBindingAsync(ct);
        snapshot.ShouldNotBeNull();

        await harness.Store.DeleteInstallAsync(snapshot!, ct);

        (await harness.BindingStore.GetAsync("slack", ct)).ShouldBeNull();

        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var mapRow = await db.TenantSlackWorkspaceMap
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.TeamId == "T-delete", ct);
            mapRow.ShouldBeNull();
        }

        harness.FakeSecretStore.Stored.ShouldNotContainKey(snapshot!.BotTokenSecretName);
        harness.FakeSecretStore.Stored.ShouldNotContainKey(snapshot.SigningSecretSecretName);
    }

    [Fact]
    public async Task PersistInstallAsync_RebindSameTeamId_UpsertsBindingAndMap()
    {
        // Re-bind without a delete is unusual but legal — the same
        // team_id refresh path. The map row's UpdatedAt would shift
        // but the row count stays at 1.
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var payload1 = new SlackInstallPayload("T-re", "First", "U-bot", "xoxb-1", "sig", "U-installer", null);
        var payload2 = new SlackInstallPayload("T-re", "Second", "U-bot", "xoxb-2", "sig", "U-installer", null);

        await harness.Store.PersistInstallAsync(payload1, ct);
        await harness.Store.PersistInstallAsync(payload2, ct);

        using var scope = harness.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.TenantSlackWorkspaceMap
            .IgnoreQueryFilters()
            .Where(m => m.TeamId == "T-re")
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].TeamName.ShouldBe("Second");
    }

    [Fact]
    public async Task ReadBotTokenAsync_ReturnsStoredPlaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        var harness = TestHarness.Create();

        var payload = new SlackInstallPayload(
            "T-read", null, "U-bot", "xoxb-secret-value", "sig", "U-installer", null);
        await harness.Store.PersistInstallAsync(payload, ct);

        var snapshot = await harness.Store.GetExistingBindingAsync(ct);
        snapshot.ShouldNotBeNull();

        var token = await harness.Store.ReadBotTokenAsync(snapshot!, ct);
        token.ShouldBe("xoxb-secret-value");
    }

    // ---- Harness ----

    private sealed class TestHarness
    {
        public IServiceProvider Provider { get; }
        public SlackInstallStore Store { get; }
        public ITenantConnectorBindingStore BindingStore { get; }
        public FakeSecretStore FakeSecretStore { get; }

        public TestHarness(
            IServiceProvider provider,
            SlackInstallStore store,
            ITenantConnectorBindingStore bindingStore,
            FakeSecretStore secretStore)
        {
            Provider = provider;
            Store = store;
            BindingStore = bindingStore;
            FakeSecretStore = secretStore;
        }

        public static TestHarness Create()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TestTenantId);
            services.AddSingleton(tenantContext);

            var dbName = $"SlackInstallStoreTests_{Guid.NewGuid():N}";
            services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));

            services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
            services.AddScoped<ITenantConnectorBindingRepository, TenantConnectorBindingRepository>();
            services.AddSingleton<ITenantConnectorBindingStore, TenantConnectorBindingStore>();

            var fakeStore = new FakeSecretStore();
            var fakeRegistry = new FakeSecretRegistry(fakeStore, tenantContext);
            services.AddSingleton<ISecretStore>(fakeStore);
            services.AddSingleton<ISecretRegistry>(fakeRegistry);
            services.AddSingleton<ISecretResolver>(new FakeSecretResolver(fakeStore, fakeRegistry));

            services.TryAddSingleton<SlackInstallStore>();

            var provider = services.BuildServiceProvider();
            return new TestHarness(
                provider,
                provider.GetRequiredService<SlackInstallStore>(),
                provider.GetRequiredService<ITenantConnectorBindingStore>(),
                fakeStore);
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ISecretStore"/>. Tracks every
    /// stored plaintext by the structural secret name (we do not need
    /// opaque store-key indirection in tests).
    /// </summary>
    internal sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

        // Opaque store key → plaintext.
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public Task<string> WriteAsync(string plaintext, CancellationToken ct)
        {
            var key = Guid.NewGuid().ToString("N");
            _byKey[key] = plaintext;
            return Task.FromResult(key);
        }

        public Task<string?> ReadAsync(string storeKey, CancellationToken ct)
            => Task.FromResult(_byKey.TryGetValue(storeKey, out var v) ? v : null);

        public Task DeleteAsync(string storeKey, CancellationToken ct)
        {
            _byKey.Remove(storeKey);
            return Task.CompletedTask;
        }

        public void Index(string secretName, string plaintext) => Stored[secretName] = plaintext;
        public void Unindex(string secretName) => Stored.Remove(secretName);
    }

    /// <summary>
    /// Minimal in-memory registry covering the surface the install
    /// store actually uses (Register / DeleteAsync / LookupStoreKeyAsync).
    /// Other registry methods throw — the SlackInstallStore path must
    /// not touch them.
    /// </summary>
    internal sealed class FakeSecretRegistry : ISecretRegistry
    {
        private readonly FakeSecretStore _store;
        private readonly ITenantContext _tenantContext;
        private readonly Dictionary<(Guid, SecretScope, Guid?, string), string> _refs = new();

        public FakeSecretRegistry(FakeSecretStore store, ITenantContext tenantContext)
        {
            _store = store;
            _tenantContext = tenantContext;
        }

        public Task RegisterAsync(SecretRef @ref, string storeKey, SecretOrigin origin, CancellationToken ct)
        {
            var key = (_tenantContext.CurrentTenantId, @ref.Scope, @ref.OwnerId, @ref.Name);
            _refs[key] = storeKey;
            // The fake also indexes by structural name for test assertions.
            var plaintext = _store.ReadAsync(storeKey, ct).Result ?? string.Empty;
            _store.Index(@ref.Name, plaintext);
            return Task.CompletedTask;
        }

        // ISecretRegistry 5-arg overload (with the propagate flag,
        // #1737). The install store always uses the 4-arg form; the
        // fake forwards to it for completeness.
        public Task RegisterAsync(SecretRef @ref, string storeKey, SecretOrigin origin, bool propagate, CancellationToken ct)
            => RegisterAsync(@ref, storeKey, origin, ct);

        public Task<string?> LookupStoreKeyAsync(SecretRef @ref, CancellationToken ct)
        {
            var key = (_tenantContext.CurrentTenantId, @ref.Scope, @ref.OwnerId, @ref.Name);
            return Task.FromResult(_refs.TryGetValue(key, out var storeKey) ? storeKey : null);
        }

        public Task DeleteAsync(SecretRef @ref, CancellationToken ct)
        {
            var key = (_tenantContext.CurrentTenantId, @ref.Scope, @ref.OwnerId, @ref.Name);
            _refs.Remove(key);
            _store.Unindex(@ref.Name);
            return Task.CompletedTask;
        }

        // Not used by the install-store path; throw to surface
        // accidental use.
        public Task<SecretPointer?> LookupAsync(SecretRef @ref, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(
            SecretRef @ref, int? version, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(
            SecretRef @ref, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool?> LookupPropagateAsync(SecretRef @ref, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, Guid? ownerId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SecretVersionInfo>> ListVersionsAsync(SecretRef @ref, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<SecretRotation> RotateAsync(
            SecretRef @ref,
            string newStoreKey,
            SecretOrigin newOrigin,
            Func<string, CancellationToken, Task>? deletePreviousStoreKeyAsync,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> PruneAsync(
            SecretRef @ref, int keep, Func<string, CancellationToken, Task>? deletePrunedStoreKeyAsync,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Minimal resolver that consults the fake registry. Used by
    /// <c>ReadBotTokenAsync</c> only — other resolver paths throw.
    /// </summary>
    internal sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly FakeSecretStore _store;
        private readonly FakeSecretRegistry _registry;

        public FakeSecretResolver(FakeSecretStore store, FakeSecretRegistry registry)
        {
            _store = store;
            _registry = registry;
        }

        public Task<string?> ResolveAsync(SecretRef @ref, CancellationToken ct)
            => ResolveValueAsync(@ref, ct);

        public async Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, CancellationToken ct)
        {
            var value = await ResolveValueAsync(@ref, ct);
            return new SecretResolution(
                Value: value,
                Path: value is null ? SecretResolvePath.NotFound : SecretResolvePath.Direct,
                EffectiveRef: value is null ? null : @ref);
        }

        public Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, int? version, CancellationToken ct)
            => ResolveWithPathAsync(@ref, ct);

        public Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, Guid? ownerId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SecretRef>>(Array.Empty<SecretRef>());

        private async Task<string?> ResolveValueAsync(SecretRef @ref, CancellationToken ct)
        {
            var key = await _registry.LookupStoreKeyAsync(@ref, ct);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            return await _store.ReadAsync(key, ct);
        }
    }
}
