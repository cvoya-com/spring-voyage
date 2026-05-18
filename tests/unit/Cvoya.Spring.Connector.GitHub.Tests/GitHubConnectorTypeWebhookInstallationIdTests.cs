// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the #2429 fix — the GitHub connector persists the
/// installation id alongside the webhook hook id at register time, and uses
/// that persisted value on teardown even when the operator has re-PUTed the
/// binding to a different installation in between.
///
/// <para>
/// v0.1 has no released deployments, so there is no legacy-row migration
/// shim: the register path resolves the binding's
/// <see cref="UnitGitHubConfig.AppInstallationId"/> through the connector's
/// global default and persists the resolved (non-null) value, and the
/// teardown path throws when the persisted id is null/missing rather than
/// silently falling back to the current binding.
/// </para>
/// </summary>
public class GitHubConnectorTypeWebhookInstallationIdTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IUnitConnectorRuntimeStore _runtimeStore;
    private readonly IGitHubWebhookRegistrar _webhookRegistrar;
    private readonly IGitHubInstallationsClient _installationsClient;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;

    public GitHubConnectorTypeWebhookInstallationIdTests()
    {
        _configStore = Substitute.For<IUnitConnectorConfigStore>();
        _runtimeStore = Substitute.For<IUnitConnectorRuntimeStore>();
        _webhookRegistrar = Substitute.For<IGitHubWebhookRegistrar>();
        _installationsClient = Substitute.For<IGitHubInstallationsClient>();
        _logger = Substitute.For<ILogger>();
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
    }

    [Fact]
    public async Task OnUnitStartingAsync_PersistsHookIdAndInstallationId()
    {
        // Pin the runtime row shape — both ids are written so OnUnitStopping
        // can pick the right installation id without re-reading the binding
        // (#2429).
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(runtimeStore: runtimeStore);
        StubBinding(unitId: "unit-1", config: new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: 5150));
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(4242L);

        await sut.OnUnitStartingAsync("unit-1", TestContext.Current.CancellationToken);

        var persisted = await runtimeStore.GetAsync("unit-1", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        var runtime = persisted!.Value.Deserialize<GitHubConnectorRuntime>(ConfigJson);
        runtime.ShouldNotBeNull();
        runtime!.HookId.ShouldBe(4242L);
        runtime.InstallationId.ShouldBe(5150L);
    }

    [Fact]
    public async Task OnUnitStartingAsync_NullBindingInstallationId_FallsBackToGlobalAndPersistsResolved()
    {
        // When the binding's AppInstallationId is null, the connector
        // resolves through the global default (per #2385) and persists
        // *that* value on the runtime row. The persisted id is therefore
        // non-null on every fresh row — no null sneaks through to teardown.
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(runtimeStore: runtimeStore, globalInstallationId: 8080);
        StubBinding(unitId: "unit-globalfallback", config: new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: null));
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(99L);

        await sut.OnUnitStartingAsync("unit-globalfallback", TestContext.Current.CancellationToken);

        // The registrar receives the resolved global id, not the binding's null.
        await _webhookRegistrar.Received(1).RegisterAsync(
            "acme", "platform", 8080L, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>());

        var persisted = await runtimeStore.GetAsync("unit-globalfallback", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        var runtime = persisted!.Value.Deserialize<GitHubConnectorRuntime>(ConfigJson);
        runtime.ShouldNotBeNull();
        runtime!.HookId.ShouldBe(99L);
        runtime.InstallationId.ShouldBe(8080L);
    }

    [Fact]
    public async Task OnUnitStoppingAsync_UsesPersistedInstallationId_NotCurrentBinding()
    {
        // The bug #2429 fix in action — the operator re-PUTs the binding to a
        // different installation id between start and stop, and the teardown
        // call MUST authenticate against the installation that created the
        // hook (persisted in the runtime row), not the new one currently on
        // the binding.
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore: configStore, runtimeStore: runtimeStore);

        // Initial binding: installation 5150.
        await configStore.SetAsync(
            "unit-1",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 5150)),
            TestContext.Current.CancellationToken);
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(4242L);
        await sut.OnUnitStartingAsync("unit-1", TestContext.Current.CancellationToken);

        // Operator re-PUTs the binding pointing at a different installation.
        await configStore.SetAsync(
            "unit-1",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 9999)),
            TestContext.Current.CancellationToken);

        await sut.OnUnitStoppingAsync("unit-1", TestContext.Current.CancellationToken);

        // The teardown call MUST use 5150 (the installation that created the
        // hook), not 9999 (the current binding's value).
        await _webhookRegistrar.Received(1).UnregisterAsync(
            "acme",
            "platform",
            4242L,
            5150L,
            Arg.Any<CancellationToken>());
        await _webhookRegistrar.DidNotReceive().UnregisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(),
            9999L,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"hookId": 1234}""", "missing-field")]
    [InlineData("""{"hookId": 1234, "installationId": null}""", "explicit-null")]
    public async Task OnUnitStoppingAsync_CorruptRuntimeRow_Throws(string runtimeJson, string label)
    {
        // v0.1 has no released deployments — there are no pre-#2429 runtime
        // rows in the wild. A row with null/missing `installationId` is
        // therefore corrupt: fail loudly so operators can pinpoint and
        // repair it, rather than silently fall back to the binding's current
        // AppInstallationId (which may target a different installation that
        // cannot delete the hook). Both shapes — missing field and explicit
        // null — surface the same exception.
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore: configStore, runtimeStore: runtimeStore);
        var unitId = $"unit-corrupt-{label}";

        await configStore.SetAsync(
            unitId,
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 7777)),
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(runtimeJson);
        await runtimeStore.SetAsync(
            unitId,
            doc.RootElement.Clone(),
            TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.OnUnitStoppingAsync(unitId, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(unitId);
        ex.Message.ShouldContain("acme/platform");
        ex.Message.ShouldContain("1234");

        // Teardown must not have been attempted on a corrupt row.
        await _webhookRegistrar.DidNotReceive().UnregisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnUnitLifecycle_RoundTrip_UnregisterReceivesSameInstallationIdAsRegister()
    {
        // Round-trip pin: a full register → stop pair on the same binding
        // hands the same installation id to both register and unregister.
        // This is the canonical "no-op operator" case the in-memory fakes
        // exercise end-to-end so we have an integration-shaped assertion
        // even in the unit suite.
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore: configStore, runtimeStore: runtimeStore);

        await configStore.SetAsync(
            "unit-rt",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 1234567)),
            TestContext.Current.CancellationToken);

        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(31415L);
        await sut.OnUnitStartingAsync("unit-rt", TestContext.Current.CancellationToken);
        await sut.OnUnitStoppingAsync("unit-rt", TestContext.Current.CancellationToken);

        await _webhookRegistrar.Received(1).RegisterAsync(
            "acme", "platform", 1234567L, Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>());
        await _webhookRegistrar.Received(1).UnregisterAsync(
            "acme", "platform", 31415L, 1234567L,
            Arg.Any<CancellationToken>());

        // Runtime row cleared on successful teardown.
        var after = await runtimeStore.GetAsync("unit-rt", TestContext.Current.CancellationToken);
        after.ShouldBeNull();
    }

    private void StubBinding(string unitId, UnitGitHubConfig config)
    {
        var serialized = JsonSerializer.SerializeToElement(config, ConfigJson);
        _configStore.GetAsync(unitId, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, serialized));
    }

    private static JsonElement SerializeConfig(UnitGitHubConfig config)
        => JsonSerializer.SerializeToElement(config, ConfigJson);

    private GitHubConnectorType CreateSut(
        IUnitConnectorConfigStore? configStore = null,
        IUnitConnectorRuntimeStore? runtimeStore = null,
        long? globalInstallationId = null)
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = TestPemKey.Value,
            WebhookSecret = "test-secret",
            WebhookUrl = "https://example.com/api/v1/webhooks/github",
            InstallationId = globalInstallationId,
        };
        var optionsAccessor = Options.Create(options);
        var requirement = new GitHubAppConfigurationRequirement(optionsAccessor);

        var sp = new ServiceCollection().BuildServiceProvider();

        return new GitHubConnectorType(
            configStore ?? _configStore,
            runtimeStore ?? _runtimeStore,
            _webhookRegistrar,
            _installationsClient,
            Substitute.For<IGitHubCollaboratorsClient>(),
            optionsAccessor,
            requirement,
            Substitute.For<IOAuthSessionStore>(),
            sp,
            _loggerFactory);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IUnitConnectorConfigStore"/> for the
    /// lifecycle tests. Mirrors the helper in
    /// <see cref="GitHubConnectorTypeOnUnitStartingEventsTests"/>; kept
    /// per-file so the fixtures stay independent.
    /// </summary>
    private sealed class InMemoryConfigStore : IUnitConnectorConfigStore
    {
        private readonly Dictionary<string, UnitConnectorBinding> _bindings = new();

        public Task<UnitConnectorBinding?> GetAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.FromResult(_bindings.TryGetValue(unitId, out var b) ? b : null);

        public Task SetAsync(string unitId, Guid typeId, JsonElement config, CancellationToken cancellationToken = default)
        {
            _bindings[unitId] = new UnitConnectorBinding(typeId, config);
            return Task.CompletedTask;
        }

        public Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
        {
            _bindings.Remove(unitId);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryRuntimeStore : IUnitConnectorRuntimeStore
    {
        private readonly Dictionary<string, JsonElement> _runtime = new();

        public Task<JsonElement?> GetAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(_runtime.TryGetValue(unitId, out var r) ? r : null);

        public Task SetAsync(string unitId, JsonElement metadata, CancellationToken cancellationToken = default)
        {
            _runtime[unitId] = metadata;
            return Task.CompletedTask;
        }

        public Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
        {
            _runtime.Remove(unitId);
            return Task.CompletedTask;
        }
    }
}
