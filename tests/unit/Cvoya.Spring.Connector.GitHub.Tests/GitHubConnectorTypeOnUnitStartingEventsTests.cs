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
/// Coverage for <see cref="GitHubConnectorType.OnUnitStartingAsync"/> — pins
/// that the per-unit <see cref="UnitGitHubConfig.Events"/> list flows through
/// to the webhook registrar (issue #2423). The credential-validation tests
/// already cover the rest of the connector-type surface; this fixture exists
/// purely so the events plumbing is exercised end-to-end at the
/// <c>IConnectorType</c> seam.
/// </summary>
public class GitHubConnectorTypeOnUnitStartingEventsTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web);

    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IUnitConnectorRuntimeStore _runtimeStore;
    private readonly IGitHubWebhookRegistrar _webhookRegistrar;
    private readonly IGitHubInstallationsClient _installationsClient;
    private readonly ILoggerFactory _loggerFactory;

    public GitHubConnectorTypeOnUnitStartingEventsTests()
    {
        _configStore = Substitute.For<IUnitConnectorConfigStore>();
        _runtimeStore = Substitute.For<IUnitConnectorRuntimeStore>();
        _webhookRegistrar = Substitute.For<IGitHubWebhookRegistrar>();
        _installationsClient = Substitute.For<IGitHubInstallationsClient>();
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    [Fact]
    public async Task OnUnitStartingAsync_WithCustomEvents_PassesEventsThroughToRegistrar()
    {
        // Issue #2423: the binding's Events list is honoured on start. Pin
        // the exact list the connector forwards so a regression that drops
        // back to the registrar default would fail.
        var sut = CreateSut();
        var customEvents = new[] { "issues", "pull_request" };
        StubBinding(unitId: "unit-1", config: new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: 5150,
            Events: customEvents));
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(4242L);

        await sut.OnUnitStartingAsync("unit-1", TestContext.Current.CancellationToken);

        await _webhookRegistrar.Received(1).RegisterAsync(
            "acme",
            "platform",
            5150,
            Arg.Is<IReadOnlyList<string>?>(events =>
                events != null &&
                events.Count == 2 &&
                events.Contains("issues") &&
                events.Contains("pull_request")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnUnitStartingAsync_WithNullEvents_PassesNullThroughToRegistrar()
    {
        // The connector does not paper over null on the way in — the
        // registrar is the contract owner that maps null → default set.
        // Pinning the null pass-through here keeps the responsibility split
        // explicit so future refactors don't duplicate the fallback.
        var sut = CreateSut();
        StubBinding(unitId: "unit-2", config: new UnitGitHubConfig(
            Owner: "acme",
            Repo: "platform",
            AppInstallationId: 5150,
            Events: null));
        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(4243L);

        await sut.OnUnitStartingAsync("unit-2", TestContext.Current.CancellationToken);

        await _webhookRegistrar.Received(1).RegisterAsync(
            "acme",
            "platform",
            5150,
            Arg.Is<IReadOnlyList<string>?>(events => events == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnUnitStartingAsync_StopStartCycle_PicksUpChangedEvents()
    {
        // Issue #2423: a Running unit that has its Events list rewritten
        // (via PutConfigAsync) MUST pick up the new list when it next
        // starts. Simulate the full stop → re-bind → start path against the
        // in-memory store fakes so the test exercises the actual data flow,
        // not just the registrar call shape.
        var configStore = new InMemoryConfigStore();
        var runtimeStore = new InMemoryRuntimeStore();
        var sut = CreateSut(configStore, runtimeStore);

        // Initial binding: subscribe to "issues" only.
        await configStore.SetAsync(
            "unit-3",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 5150,
                Events: new[] { "issues" })),
            TestContext.Current.CancellationToken);

        _webhookRegistrar.RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(100L, 101L);

        // First start: hook subscribes to ["issues"].
        await sut.OnUnitStartingAsync("unit-3", TestContext.Current.CancellationToken);

        // Stop: the runtime store clears so the next start re-registers.
        await sut.OnUnitStoppingAsync("unit-3", TestContext.Current.CancellationToken);

        // Operator rewrites the binding to ["pull_request", "issue_comment"]
        // through the typed PUT — emulate the persisted config update
        // directly (the actual PUT path is covered elsewhere).
        await configStore.SetAsync(
            "unit-3",
            GitHubConnectorType.GitHubTypeId,
            SerializeConfig(new UnitGitHubConfig(
                Owner: "acme",
                Repo: "platform",
                AppInstallationId: 5150,
                Events: new[] { "pull_request", "issue_comment" })),
            TestContext.Current.CancellationToken);

        // Second start: hook subscribes to the new list.
        await sut.OnUnitStartingAsync("unit-3", TestContext.Current.CancellationToken);

        var calls = _webhookRegistrar.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IGitHubWebhookRegistrar.RegisterAsync))
            .ToList();
        calls.Count.ShouldBe(2);

        var firstEvents = (IReadOnlyList<string>?)calls[0].GetArguments()[3];
        firstEvents.ShouldNotBeNull();
        firstEvents!.Count.ShouldBe(1);
        firstEvents.ShouldContain("issues");

        var secondEvents = (IReadOnlyList<string>?)calls[1].GetArguments()[3];
        secondEvents.ShouldNotBeNull();
        secondEvents!.Count.ShouldBe(2);
        secondEvents.ShouldContain("pull_request");
        secondEvents.ShouldContain("issue_comment");
        secondEvents.ShouldNotContain("issues");
    }

    private void StubBinding(string unitId, UnitGitHubConfig config)
    {
        var serialized = JsonSerializer.SerializeToElement(config, ConfigJson);
        _configStore.GetAsync(unitId, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(GitHubConnectorType.GitHubTypeId, serialized));
    }

    private static JsonElement SerializeConfig(UnitGitHubConfig config)
        => JsonSerializer.SerializeToElement(config, ConfigJson);

    private GitHubConnectorType CreateSut()
        => CreateSut(_configStore, _runtimeStore);

    private GitHubConnectorType CreateSut(
        IUnitConnectorConfigStore configStore,
        IUnitConnectorRuntimeStore runtimeStore)
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
            configStore,
            runtimeStore,
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
    /// stop/start-cycle test. Keeps the test independent of the actor-store
    /// implementation while exercising the actual GetAsync / SetAsync data
    /// flow the connector type relies on.
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
