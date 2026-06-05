// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for issue #2514 — webhook-translated messages must carry a
/// stable Guid-shaped <c>ThreadId</c> so the persistent A2A dispatch path
/// can mint a per-message <c>SPRING_CALLBACK_TOKEN</c> (#1943). The id is
/// the <see cref="IThreadRegistry"/> participant-set thread for the
/// <c>(connector://github, unit://&lt;bindingUnitId&gt;)</c> pair, so
/// successive deliveries from the same binding thread under the same
/// conversation.
/// </summary>
public class GitHubWebhookHandlerThreadIdTests
{
    private static readonly string UnitHexA =
        new Guid("aaaaaaaa-0000-0000-0000-000000000003").ToString("N");
    private static readonly string UnitHexB =
        new Guid("bbbbbbbb-0000-0000-0000-000000000004").ToString("N");

    [Fact]
    public async Task TranslateEventAsync_StampsParticipantSetThreadId_PerBinding()
    {
        var registry = new RecordingThreadRegistry();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: LookupReturning(
                UnitHexA, new UnitGitHubConfig(Repo: "acme/platform")),
            scopeFactory: ScopeFactoryWith(registry));

        var payload = BuildIssuePayload(owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].ThreadId.ShouldNotBeNull();
        GuidFormatter.TryParse(messages[0].ThreadId!, out _).ShouldBeTrue();
        registry.Calls.Count.ShouldBe(1);
        var (participants, _) = registry.Calls[0];
        participants.Count.ShouldBe(2);
        participants.ShouldContain(a => a.Scheme == "connector");
        participants.ShouldContain(a => a.Scheme == "unit" && a.Path == UnitHexA);
    }

    [Fact]
    public async Task TranslateEventAsync_SameBinding_TwoDeliveries_ReuseThreadId()
    {
        var registry = new RecordingThreadRegistry();
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: LookupReturning(
                UnitHexA, new UnitGitHubConfig(Repo: "acme/platform")),
            scopeFactory: ScopeFactoryWith(registry));

        var firstPayload = BuildIssuePayload(owner: "acme", repo: "platform");
        var secondPayload = BuildIssuePayload(owner: "acme", repo: "platform");

        var first = await handler.TranslateEventAsync(
            "issues", firstPayload, TestContext.Current.CancellationToken);
        var second = await handler.TranslateEventAsync(
            "issues", secondPayload, TestContext.Current.CancellationToken);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        // Registry collapses the same participant set to the same id —
        // successive webhook deliveries from one binding thread under the
        // same conversation.
        second[0].ThreadId.ShouldBe(first[0].ThreadId);
    }

    [Fact]
    public async Task TranslateEventAsync_DistinctBindings_GetDistinctThreadIds()
    {
        var registry = new RecordingThreadRegistry();
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitConnectorBindingEntry(
                    UnitHexA, BindingFor(new UnitGitHubConfig(Repo: "acme/platform"))),
                new UnitConnectorBindingEntry(
                    UnitHexB, BindingFor(new UnitGitHubConfig(Repo: "acme/platform"))),
            });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup,
            scopeFactory: ScopeFactoryWith(registry));

        var payload = BuildIssuePayload(owner: "acme", repo: "platform");
        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(2);
        messages[0].ThreadId.ShouldNotBeNull();
        messages[1].ThreadId.ShouldNotBeNull();
        messages[0].ThreadId.ShouldNotBe(messages[1].ThreadId);
    }

    [Fact]
    public async Task TranslateEventAsync_NoScopeFactory_FallsThroughWithNullThreadId()
    {
        // Pure-unit-test fixture without DI wiring: the handler logs a
        // warning and emits ThreadId: null so existing tests that never
        // wired a scope factory continue to compile and run. Real hosts
        // always wire IServiceScopeFactory.
        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: LookupReturning(
                UnitHexA, new UnitGitHubConfig(Repo: "acme/platform")));

        var payload = BuildIssuePayload(owner: "acme", repo: "platform");
        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].ThreadId.ShouldBeNull();
    }

    private static IServiceScopeFactory ScopeFactoryWith(IThreadRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private static IUnitConnectorBindingLookup LookupReturning(
        string unitHex, UnitGitHubConfig config)
    {
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(unitHex, BindingFor(config)) });
        return lookup;
    }

    private static UnitConnectorBinding BindingFor(UnitGitHubConfig config) =>
        new(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));

    private static JsonElement BuildIssuePayload(string owner, string repo)
    {
        var data = new
        {
            action = "opened",
            installation = new { id = 42L },
            repository = new
            {
                name = repo,
                full_name = $"{owner}/{repo}",
                owner = new { login = owner },
            },
            issue = new
            {
                number = 1,
                title = "x",
                body = "y",
                labels = Array.Empty<object>(),
            },
        };
        return JsonSerializer.SerializeToElement(data);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IThreadRegistry"/>: canonicalises the
    /// participant set (scheme + path, ordinal) and reuses the same Guid
    /// for repeated lookups so tests can assert idempotency.
    /// </summary>
    private sealed class RecordingThreadRegistry : IThreadRegistry
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public List<(IReadOnlyList<Address> Participants, string ThreadId)> Calls { get; } = new();

        public Task<string> GetOrCreateAsync(
            IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var snapshot = participants.ToList();
            var key = string.Join("|", snapshot
                .Select(a => $"{a.Scheme}://{a.Path}")
                .OrderBy(s => s, StringComparer.Ordinal));
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = GuidFormatter.Format(Guid.NewGuid());
                _byKey[key] = id;
            }
            Calls.Add((snapshot, id));
            return Task.FromResult(id);
        }

        public Task<ThreadRegistryEntry?> ResolveAsync(
            string threadId, CancellationToken cancellationToken = default)
            => Task.FromResult<ThreadRegistryEntry?>(null);

        public Task<string> EnsureThreadAsync(
            string threadId, IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var snapshot = participants.ToList();
            var key = string.Join("|", snapshot
                .Select(a => $"{a.Scheme}://{a.Path}")
                .OrderBy(s => s, StringComparer.Ordinal));
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = string.IsNullOrWhiteSpace(threadId) ? GuidFormatter.Format(Guid.NewGuid()) : threadId;
                _byKey[key] = id;
            }
            Calls.Add((snapshot, id));
            return Task.FromResult(id);
        }
    }
}
