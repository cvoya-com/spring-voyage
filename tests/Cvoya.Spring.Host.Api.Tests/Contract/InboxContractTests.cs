// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;

using Cvoya.Spring.Core.Observability;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/tenant/inbox</c> surface
/// (closes #1255 / C1.3). The inbox endpoint delegates to
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
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>
            {
                new(
                    ThreadId: "contract-inbox-thread",
                    From: "agent://contract-bot",
                    Human: "human://local-dev-user",
                    PendingSince: now,
                    Summary: "Contract inbox test item"),
            });

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/inbox", "get", "200", body);
    }

    [Fact]
    public async Task ListInbox_EmptyInbox_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.ThreadQueryService.ClearSubstitute();
        _factory.ThreadQueryService
            .ListInboxAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<InboxItem>());

        var response = await _client.GetAsync("/api/v1/tenant/inbox", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/inbox", "get", "200", body);
    }

    /// <summary>
    /// Custom factory that swaps <see cref="IThreadQueryService"/> for a
    /// substitute — mirrors the approach used by <c>ThreadContractTests</c>.
    /// </summary>
    public sealed class Factory : CustomWebApplicationFactory
    {
        public IThreadQueryService ThreadQueryService { get; } = Substitute.For<IThreadQueryService>();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IThreadQueryService))
                    .ToList();
                foreach (var d in descriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton(ThreadQueryService);
            });
        }
    }
}