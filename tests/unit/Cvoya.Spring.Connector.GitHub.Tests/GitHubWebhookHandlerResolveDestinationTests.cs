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
/// Coverage for the ADR-0047 §10 webhook resolution path —
/// <see cref="GitHubWebhookHandler.TranslateEventAsync"/> keys on
/// <c>(owner, repo)</c> within the receiving tenant and returns one
/// translated message per matching binding (fan-out). The binding lookup
/// is tenant-scoped at the EF layer so cross-tenant rows do not surface.
/// </summary>
public class GitHubWebhookHandlerResolveDestinationTests
{
    private static readonly string UnitHexA =
        new Guid("aaaaaaaa-0000-0000-0000-000000000001").ToString("N");
    private static readonly string UnitHexB =
        new Guid("bbbbbbbb-0000-0000-0000-000000000002").ToString("N");

    [Fact]
    public async Task TranslateEventAsync_NoBindingMatches_DropsSilently()
    {
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitConnectorBindingEntry>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 999, owner: "ghost", repo: "phantom");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TranslateEventAsync_SingleBindingMatches_RoutesToUnit()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].To.Scheme.ShouldBe("unit");
        messages[0].To.Path.ShouldBe(UnitHexA);
    }

    [Fact]
    public async Task TranslateEventAsync_OwnerCaseMismatch_StillMatches()
    {
        var config = new UnitGitHubConfig(
            Repo: "Acme/Platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].To.Path.ShouldBe(UnitHexA);
    }

    [Fact]
    public async Task TranslateEventAsync_InstallationIdMismatch_StillMatchesUnderOwnerRepoKey()
    {
        // ADR-0047 §10: installation_id leaves the routing fabric. The
        // matcher keys on (owner, repo) within the receiving tenant; the
        // binding's stored installation_id is no longer part of the
        // match. Cross-tenant collisions are handled at binding-create
        // time, not at delivery time — see CrossTenantPayload_DoesNotMatch.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        // Owner / repo match; installation does not. Still matches —
        // installation is no longer a routing key.
        var payload = BuildIssuePayload(installationId: 99, owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].To.Path.ShouldBe(UnitHexA);
    }

    [Fact]
    public async Task TranslateEventAsync_RepoMismatch_Drops()
    {
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "other-repo");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TranslateEventAsync_GhWebhookForwardPayloadWithoutInstallationId_StillMatches()
    {
        // UC4 — `gh webhook forward` delivers payloads without an
        // `installation` field. The pre-ADR-0047 two-case matcher
        // dropped these; the new (owner, repo) key resolves them.
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var data = new
        {
            action = "opened",
            repository = new
            {
                name = "platform",
                full_name = "acme/platform",
                owner = new { login = "acme" },
            },
            issue = new
            {
                number = 1,
                title = "x",
                body = "y",
                labels = Array.Empty<object>(),
            },
        };
        var payload = JsonSerializer.SerializeToElement(data);

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(1);
        messages[0].To.Path.ShouldBe(UnitHexA);
    }

    [Fact]
    public async Task TranslateEventAsync_MultipleBindingsSameRepo_FansOutWithinTenant()
    {
        // ADR-0047 §10: many bindings per (tenant, owner, repo) is
        // supported. The matcher returns one message per binding; each
        // binding's per-binding filter decides processing. Load-bearing
        // example from the ADR: frontend-team + backend-team units, both
        // bound to the same monorepo, divergent label filters.
        var frontendConfig = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: 42L,
            IncludeLabels: new[] { "frontend" });
        var backendConfig = new UnitGitHubConfig(
            Repo: "acme/platform",
            AppInstallationId: 42L,
            IncludeLabels: new[] { "backend" });
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitConnectorBindingEntry(UnitHexA, BindingFor(frontendConfig)),
                new UnitConnectorBindingEntry(UnitHexB, BindingFor(backendConfig)),
            });

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.Count.ShouldBe(2);
        var addresses = messages.Select(m => m.To.Path).ToHashSet();
        addresses.ShouldContain(UnitHexA);
        addresses.ShouldContain(UnitHexB);

        // Every fanned-out message has a unique Id so downstream observers
        // see two distinct deliveries rather than collapsing on a shared key.
        messages[0].Id.ShouldNotBe(messages[1].Id);
    }

    [Fact]
    public async Task TranslateEventAsync_CrossTenantPayload_DoesNotMatchReceivingTenant()
    {
        // Security property: the binding lookup is tenant-scoped at the
        // EF layer (via the SpringDbContext query filter on
        // UnitConnectorBindingEntity). A binding for "acme/platform" in
        // another tenant is structurally invisible to a webhook arriving
        // in the receiving tenant. The handler models this by relying on
        // the lookup's tenant scoping — when the lookup returns no rows,
        // the matcher cannot mis-route the payload regardless of
        // (owner, repo) match.
        var lookup = Substitute.For<IUnitConnectorBindingLookup>();
        lookup
            .ListByConnectorTypeAsync(GitHubConnectorType.GitHubTypeId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitConnectorBindingEntry>());

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

        var payload = BuildIssuePayload(installationId: 42, owner: "acme", repo: "platform");

        var messages = await handler.TranslateEventAsync(
            "issues", payload, TestContext.Current.CancellationToken);

        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task TranslateEventAsync_PayloadWithoutRepository_Drops()
    {
        // installation / installation_repositories / projects_v2* events
        // carry no (owner, repo) coordinates. Phase E intentionally
        // drops them under the new key; the supplementary org-event
        // delivery path lands in a later phase. The matcher must NOT
        // mis-route them under fan-out (which would deliver one copy to
        // every binding in the tenant).
        var config = new UnitGitHubConfig(
            Repo: "acme/platform", AppInstallationId: 42L);
        var lookup = LookupReturning(UnitHexA, config);

        var handler = new GitHubWebhookHandler(
            NullLoggerFactory.Instance,
            bindingLookup: lookup);

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

        var messages = await handler.TranslateEventAsync(
            "installation", payload, TestContext.Current.CancellationToken);

        messages.ShouldBeEmpty();
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
