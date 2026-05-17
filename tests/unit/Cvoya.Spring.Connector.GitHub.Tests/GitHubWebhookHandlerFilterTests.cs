// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Higher-level tests for <see cref="GitHubWebhookHandler.ApplyInboundFilterAsync"/>
/// — issue #2407. Confirms that the handler loads the unit binding, applies
/// the per-binding filter, emits an audit ActivityEvent on drop, and falls
/// through cleanly when no binding / no filter exists.
/// </summary>
public class GitHubWebhookHandlerFilterTests
{
    private static readonly string TargetUnitHex = TestSlugIds.HexFor("eng-team");

    [Fact]
    public async Task ApplyInboundFilterAsync_NoBindingStoreWired_PassesThrough()
    {
        // No IUnitConnectorConfigStore — handler must behave exactly as before
        // and return the translated message unchanged.
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_NoBindingForUnit_PassesThrough()
    {
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_BindingIsNotGitHubShaped_PassesThrough()
    {
        var nonGitHubTypeId = Guid.NewGuid();
        var emptyConfig = JsonSerializer.SerializeToElement(new { foo = "bar" });
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(nonGitHubTypeId, emptyConfig));
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_FilterPasses_ReturnsMessage()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludeLabels: new[] { "spring-voyage" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "spring-voyage" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await bus.DidNotReceive().PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_FilterDrops_ReturnsNullAndEmitsActivity()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            IncludeLabels: new[] { "spring-voyage" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? publishedEvent = null;
        await bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => publishedEvent = e),
            Arg.Any<CancellationToken>());

        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "bug" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await bus.Received(1).PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
        publishedEvent.ShouldNotBeNull();
        publishedEvent!.EventType.ShouldBe(ActivityEventType.ConnectorEventFiltered);
        publishedEvent.Source.Scheme.ShouldBe("unit");
        publishedEvent.Source.Path.ShouldBe(TargetUnitHex);
        publishedEvent.Details.ShouldNotBeNull();
        var details = publishedEvent.Details!.Value;
        details.GetProperty("connector").GetString().ShouldBe("github");
        details.GetProperty("event_type").GetString().ShouldBe("issues");
        details.GetProperty("filter_kind").GetString().ShouldBe("include_label");
        details.GetProperty("filter_value").GetString().ShouldBe("spring-voyage");
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_ExcludeLabelDrops_EmitsAuditWithMatchedLabel()
    {
        var config = new UnitGitHubConfig(
            Owner: "acme", Repo: "platform",
            ExcludeLabels: new[] { "wip" });
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>()).Returns(binding);

        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e),
            Arg.Any<CancellationToken>());

        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store,
            activityEventBus: bus);
        var translated = BuildTranslatedMessage(labels: new[] { "wip", "feature" });

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        captured.ShouldNotBeNull();
        captured!.Details!.Value.GetProperty("filter_kind").GetString().ShouldBe("exclude_label");
        // ExcludeLabel reports the specific matched label.
        captured.Details!.Value.GetProperty("filter_value").GetString().ShouldBe("wip");
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_StorageError_PassesThroughUnfiltered()
    {
        var store = Substitute.For<IUnitConnectorConfigStore>();
        store.GetAsync(TargetUnitHex, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("simulated transient blip"));
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions { DefaultTargetUnitPath = TargetUnitHex },
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessage();

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        // A transient lookup failure must not silently suppress events.
        result.ShouldBe(translated);
    }

    [Fact]
    public async Task ApplyInboundFilterAsync_NonUnitDestination_PassesThrough()
    {
        // When the connector falls back to the system-router sentinel
        // (no DefaultTargetUnitPath configured) the destination scheme is
        // "system" — the filter must not attempt a lookup for that.
        var store = Substitute.For<IUnitConnectorConfigStore>();
        var handler = new GitHubWebhookHandler(
            new GitHubConnectorOptions(),
            NullLoggerFactory.Instance,
            configStore: store);
        var translated = BuildTranslatedMessageWithDestination(
            new Address("system", new Guid("00000000-0000-0000-0000-726f75746572")));

        var result = await handler.ApplyInboundFilterAsync(translated, "issues", TestContext.Current.CancellationToken);

        result.ShouldBe(translated);
        await store.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Message BuildTranslatedMessage(IReadOnlyList<string>? labels = null)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "github",
            intent = "work_assignment",
            action = "opened",
            issue = new
            {
                number = 42,
                title = "T",
                labels = labels ?? Array.Empty<string>(),
                author = "opener",
            },
        });
        return new Message(
            Id: Guid.NewGuid(),
            From: new Address("connector", new Guid("00000000-0000-0000-0000-006769746875")),
            To: new Address(Address.UnitScheme, new Guid(TargetUnitHex)),
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static Message BuildTranslatedMessageWithDestination(Address to)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            source = "github",
            action = "opened",
            issue = new { number = 1, title = "x", labels = Array.Empty<string>() },
        });
        return new Message(
            Id: Guid.NewGuid(),
            From: new Address("connector", new Guid("00000000-0000-0000-0000-006769746875")),
            To: to,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }
}
