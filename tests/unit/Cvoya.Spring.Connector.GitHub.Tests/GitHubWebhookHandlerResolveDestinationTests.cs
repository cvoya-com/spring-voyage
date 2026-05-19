// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the App-level webhook resolution path (#2456) —
/// <see cref="GitHubWebhookHandler.TranslateEventAsync"/> resolves the
/// destination unit from the inbound payload's <c>(installation_id,
/// owner, repo)</c> triple via <see cref="IUnitConnectorBindingLookup"/>,
/// or drops the delivery silently when no binding matches.
/// </summary>
public class GitHubWebhookHandlerResolveDestinationTests
{
    private static readonly string UnitHex =
        new Guid("dddddddd-0000-0000-0000-000000000001").ToString("N");

    [Fact]
    public async Task TranslateEventAsync_NoBindingMatches_DropsSilently()
    {
        // Payload references installation 999 and a repo no unit is bound
        // to. The lookup returns an empty list (or a list of bindings none
        // of which match the triple) — the handler returns null, which
        // the connector surfaces as Ignored / ACK 202 to GitHub.
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitConnectorBindingEntry>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 999, owner: "ghost", repo: "phantom");

        var message = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    [Fact]
    public async Task TranslateEventAsync_BindingMatchesTriple_RoutesToUnit()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(UnitHex, binding) });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var message = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.To.Scheme.ShouldBe("unit");
        message.To.Path.ShouldBe(UnitHex);
    }

    [Fact]
    public async Task TranslateEventAsync_OwnerCaseMismatch_StillMatches()
    {
        // GitHub login comparisons are case-insensitive.
        var config = new UnitGitHubConfig(
            Repo: "Acme/Platform", AppInstallationId: 42L);
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(UnitHex, binding) });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var message = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.To.Path.ShouldBe(UnitHex);
    }

    [Fact]
    public async Task TranslateEventAsync_InstallationIdMismatch_Drops()
    {
        // Owner / repo match but installation does not — must not match
        // (different installation could legitimately ship a same-named
        // repo, especially in personal-account fork patterns).
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(UnitHex, binding) });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 99, owner: "acme", repo: "platform");

        var message = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    [Fact]
    public async Task TranslateEventAsync_InstallationEvent_UsesInstallationFallback()
    {
        // installation / installation_repositories / projects_v2* events
        // are App-level and don't carry repository coordinates. The
        // resolver falls back to the first binding whose installation
        // matches so the operator still sees lifecycle signal.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(UnitHex, binding) });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        // installation.created payload — no repository field.
        var data = new
        {
            action = "created",
            installation = new
            {
                id = 42L,
                account = new { login = "acme", type = "Organization" },
                repository_selection = "selected",
            },
            repositories = Array.Empty<object>(),
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var message = await handler.TranslateEventAsync(
            "installation", payload, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.To.Path.ShouldBe(UnitHex);
    }

    [Fact]
    public async Task TranslateEventAsync_RepoOnlyEvent_DoesNotFallbackToInstallation()
    {
        // Repo-shaped events (issues, pull_request, …) MUST match the
        // full (installation_id, owner, repo) triple — they must not ride
        // the installation-fallback path used for App-level events. A
        // binding for repo A in installation X must not catch an event
        // for repo B in installation X.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var binding = new UnitConnectorBinding(
            GitHubConnectorType.GitHubTypeId,
            JsonSerializer.SerializeToElement(config));
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitConnectorBindingEntry(UnitHex, binding) });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        // Same installation, different repo — must drop.
        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "other-repo");

        var message = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    private static JsonElement BuildIssuePayload(long installationId, string owner, string repo)
    {
        var data = new
        {
            action = "opened",
            installation = new { id = installationId },
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
}
