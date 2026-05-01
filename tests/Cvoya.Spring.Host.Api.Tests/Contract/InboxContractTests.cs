// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Text.Json;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/tenant/inbox</c> surface
/// (closes #1255 / C1.3, #1477). The inbox endpoint delegates to
/// <see cref="IThreadQueryService.ListInboxAsync"/>; we wire a substitute so
/// these tests stay focused on wire shape without spinning up the full
/// EF projection.
/// </summary>
public class InboxContractTests : IClassFixture<InboxContractTests.Factory>
{
    private readonly Factory _factory;
    private readonly HttpClient _client;

    public InboxContractTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListInbox_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, DateTimeOffset>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new(
                    ThreadId: "contract-inbox-thread",
                    From: "agent://contract-bot",
                    Human: "human://local-dev-user",
                    PendingSince: now,
                    Summary: "Contract inbox test item",
                    UnreadCount: 2),
            });

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/inbox", "get", "200", body);

        // Verify unreadCount is present in the response.
        var doc = JsonDocument.Parse(body);
        var firstItem = doc.RootElement[0];
        firstItem.GetProperty("unreadCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task ListInbox_EmptyInbox_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, DateTimeOffset>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>());

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/inbox", "get", "200", body);
    }

    [Fact]
    public async Task ListInbox_UnreadCountZero_AfterMarkRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadId = "mark-read-test-thread";
        var now = DateTimeOffset.UtcNow;

        // Setup the thread query service to return an item with 0 unread.
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, DateTimeOffset>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new(
                    ThreadId: threadId,
                    From: "agent://contract-bot",
                    Human: "human://local-dev-user",
                    PendingSince: now,
                    Summary: "Mark-read test",
                    UnreadCount: 0),
            });

        var postResponse = await _client.PostAsync(
            $"/api/v1/tenant/inbox/{threadId}/mark-read",
            content: null,
            ct);

        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await postResponse.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("unreadCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task MarkRead_WritesTimestampToActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var threadId = "actor-write-test-thread";
        var now = DateTimeOffset.UtcNow;

        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, DateTimeOffset>?>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new(
                    ThreadId: threadId,
                    From: "agent://contract-bot",
                    Human: "human://local-dev-user",
                    PendingSince: now,
                    Summary: "Actor write test",
                    UnreadCount: 0),
            });

        await _client.PostAsync($"/api/v1/tenant/inbox/{threadId}/mark-read", content: null, ct);

        // The endpoint must have called MarkReadAsync on the human actor proxy.
        await _factory.HumanActor.Received().MarkReadAsync(
            threadId,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Custom factory that swaps <see cref="IThreadQueryService"/> and
    /// <see cref="IActorProxyFactory"/> for substitutes — mirrors the approach
    /// used by <c>ThreadContractTests</c>.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IThreadQueryService ThreadQueryService { get; } = Substitute.For<IThreadQueryService>();

        /// <summary>
        /// Substitute for the <see cref="IHumanActor"/> returned by the proxy factory.
        /// </summary>
        public IHumanActor HumanActor { get; } = Substitute.For<IHumanActor>();

        public Factory()
        {
            // Wire the human actor proxy.
            HumanActor.GetLastReadAtAsync(Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ThreadReadEntry>());
            HumanActor.MarkReadAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                // Replace the thread query service.
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IThreadQueryService))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton(ThreadQueryService);

                // Replace the actor proxy factory so MarkReadAsync calls go to
                // the HumanActor substitute above.
                var proxyDescriptors = services
                    .Where(d => d.ServiceType == typeof(IActorProxyFactory))
                    .ToList();
                foreach (var d in proxyDescriptors)
                {
                    services.Remove(d);
                }
                var proxyFactory = Substitute.For<IActorProxyFactory>();
                proxyFactory
                    .CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), nameof(HumanActor))
                    .Returns(HumanActor);
                services.AddSingleton(proxyFactory);
            });
        }
    }
}