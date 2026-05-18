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
/// Coverage for the #2429 fix — the GitHub connector persists the installation
/// id alongside the webhook hook id at register time, and uses that persisted
/// value on teardown even when the operator has re-PUTed the binding to a
/// different installation in between. Falls back to the current binding's
/// <see cref="UnitGitHubConfig.AppInstallationId"/> only for runtime rows
/// written before this PR (where the field is missing from the persisted JSON).
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
    public async Task OnUnitStartingAsync_NullInstallationId_PersistsNullInstallationId()
    {
        // The connector does not synthesise an installation id when the
        // binding does not carry one — the global-default fallback is the
        // registrar's contract and the persisted shape MUST round-trip the
        // null through to teardown so the same call path is taken.
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(runtimeStore: runtimeStore);
        StubBinding(unitId: "unit-1", config: new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: null));
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(99L);

        await sut.OnUnitStartingAsync("unit-1", TestContext.Current.CancellationToken);

        var persisted = await runtimeStore.GetAsync("unit-1", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        var runtime = persisted!.Value.Deserialize<GitHubConnectorRuntime>(ConfigJson);
        runtime.ShouldNotBeNull();
        runtime!.HookId.ShouldBe(99L);
        runtime.InstallationId.ShouldBeNull();
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

    [Fact]
    public async Task OnUnitStoppingAsync_LegacyRuntimeRow_FallsBackToCurrentBindingAndLogsWarning()
    {
        // Edge case for the bounded back-compat shim — a runtime row written
        // before #2429 has only `{"HookId": 42}` in the JSON. Deserialising
        // it leaves InstallationId == null. The teardown path falls back to
        // the binding's current AppInstallationId and logs a warning so an
        // operator stop + start cycle leaves clean rows on the next run.
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore: configStore, runtimeStore: runtimeStore);

        await configStore.SetAsync(
            "unit-legacy",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 7777)),
            TestContext.Current.CancellationToken);

        // Write a legacy-shaped runtime row directly — no InstallationId.
        using var doc = JsonDocument.Parse("""{"HookId": 1234}""");
        await runtimeStore.SetAsync(
            "unit-legacy",
            doc.RootElement.Clone(),
            TestContext.Current.CancellationToken);

        await sut.OnUnitStoppingAsync("unit-legacy", TestContext.Current.CancellationToken);

        // Fallback: the binding's current installation id is what the
        // registrar receives. This is the documented best-effort path and
        // matches the pre-#2429 behaviour for already-deployed units.
        await _webhookRegistrar.Received(1).UnregisterAsync(
            "acme",
            "platform",
            1234L,
            7777L,
            Arg.Any<CancellationToken>());

        // Warning must be emitted so the operator notices a legacy row that
        // will resolve itself on the next stop + start cycle.
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state!.ToString()!.Contains("legacy row written before #2429")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task OnUnitStoppingAsync_PersistedRowWithNullInstallationId_UsesNullAndDoesNotWarn()
    {
        // A binding bound without an AppInstallationId persists the null
        // verbatim on the runtime row — that is the documented OSS fallback
        // and is NOT a legacy row, so no warning and no fallback re-read of
        // the binding's current value (which is also null here anyway).
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore: configStore, runtimeStore: runtimeStore);

        await configStore.SetAsync(
            "unit-nullinst",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: null)),
            TestContext.Current.CancellationToken);

        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(55L);
        await sut.OnUnitStartingAsync("unit-nullinst", TestContext.Current.CancellationToken);

        await sut.OnUnitStoppingAsync("unit-nullinst", TestContext.Current.CancellationToken);

        await _webhookRegistrar.Received(1).UnregisterAsync(
            "acme",
            "platform",
            55L,
            (long?)null,
            Arg.Any<CancellationToken>());

        // Persisted row had the field — no legacy-row warning expected.
        _logger.DidNotReceive().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state!.ToString()!.Contains("legacy row written before #2429")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
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
        IUnitConnectorRuntimeStore? runtimeStore = null)
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = TestPemKey.Value,
            WebhookSecret = "test-secret",
            WebhookUrl = "https://example.com/api/v1/webhooks/github",
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
