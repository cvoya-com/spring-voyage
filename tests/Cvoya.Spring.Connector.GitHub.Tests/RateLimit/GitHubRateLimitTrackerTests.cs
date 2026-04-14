// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using System.Net.Http;

using Cvoya.Spring.Connector.GitHub.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class GitHubRateLimitTrackerTests
{
    private static GitHubRateLimitTracker CreateTracker(
        FakeTimeProvider? time = null,
        GitHubRetryOptions? options = null) =>
        new(
            options ?? new GitHubRetryOptions(),
            NullLoggerFactory.Instance,
            time);

    private static HttpResponseMessage ResponseWithQuota(
        string resource,
        int limit,
        int remaining,
        DateTimeOffset reset)
    {
        var response = new HttpResponseMessage();
        response.Headers.Add("x-ratelimit-limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-resource", resource);
        return response;
    }

    [Fact]
    public void UpdateFromHeaders_ValidHeaders_ExposesQuotaThroughGetQuota()
    {
        var tracker = CreateTracker();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        using var response = ResponseWithQuota("core", limit: 5000, remaining: 4999, reset);
        tracker.UpdateFromHeaders(response.Headers);

        var quota = tracker.GetQuota("core");
        quota.ShouldNotBeNull();
        quota!.Resource.ShouldBe("core");
        quota.Limit.ShouldBe(5000);
        quota.Remaining.ShouldBe(4999);
        quota.Reset.ToUnixTimeSeconds().ShouldBe(reset.ToUnixTimeSeconds());
    }

    [Fact]
    public void UpdateFromHeaders_MissingHeaders_DoesNotUpdate()
    {
        var tracker = CreateTracker();

        using var response = new HttpResponseMessage();
        tracker.UpdateFromHeaders(response.Headers);

        tracker.GetQuota("core").ShouldBeNull();
    }

    [Fact]
    public void UpdateFromHeaders_MissingResourceHeader_DefaultsToCore()
    {
        var tracker = CreateTracker();

        using var response = new HttpResponseMessage();
        response.Headers.Add("x-ratelimit-limit", "60");
        response.Headers.Add("x-ratelimit-remaining", "59");
        response.Headers.Add("x-ratelimit-reset", DateTimeOffset.UtcNow.AddMinutes(60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        tracker.UpdateFromHeaders(response.Headers);

        tracker.GetQuota("core").ShouldNotBeNull();
    }

    [Fact]
    public async Task WaitIfNeededAsync_AboveThreshold_ReturnsImmediately()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var tracker = CreateTracker(time, new GitHubRetryOptions { PreflightSafetyThreshold = 10 });
        using var response = ResponseWithQuota("core", 5000, remaining: 100, time.GetUtcNow().AddHours(1));
        tracker.UpdateFromHeaders(response.Headers);

        var task = tracker.WaitIfNeededAsync("core", TestContext.Current.CancellationToken);

        await task;
        task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitIfNeededAsync_BelowThreshold_DelaysUntilReset()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var tracker = CreateTracker(time, new GitHubRetryOptions { PreflightSafetyThreshold = 10 });
        var reset = start.AddSeconds(30);
        using var response = ResponseWithQuota("core", 5000, remaining: 1, reset);
        tracker.UpdateFromHeaders(response.Headers);

        var waitTask = tracker.WaitIfNeededAsync("core", TestContext.Current.CancellationToken);

        waitTask.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(30));
        await waitTask;
        waitTask.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task WaitIfNeededAsync_NoObservedQuota_ReturnsImmediately()
    {
        var tracker = CreateTracker();

        await tracker.WaitIfNeededAsync("core", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitIfNeededAsync_ResetInPast_ReturnsImmediately()
    {
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var tracker = CreateTracker(time, new GitHubRetryOptions { PreflightSafetyThreshold = 10 });
        using var response = ResponseWithQuota("core", 5000, remaining: 1, start.AddSeconds(-5));
        tracker.UpdateFromHeaders(response.Headers);

        await tracker.WaitIfNeededAsync("core", TestContext.Current.CancellationToken);
    }

    [Fact]
    public void UpdateFromHeaders_ConcurrentCalls_ConvergeToValidState()
    {
        var tracker = CreateTracker();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        Parallel.For(0, 100, i =>
        {
            using var response = ResponseWithQuota("core", 5000, remaining: 5000 - i, reset);
            tracker.UpdateFromHeaders(response.Headers);
        });

        var quota = tracker.GetQuota("core");
        quota.ShouldNotBeNull();
        quota!.Limit.ShouldBe(5000);
        // Remaining is whichever update won last; must be in the valid range.
        quota.Remaining.ShouldBeInRange(4900, 5000);
    }

    [Fact]
    public void GetQuota_DifferentResources_Isolated()
    {
        var tracker = CreateTracker();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        using (var core = ResponseWithQuota("core", 5000, 4999, reset))
        {
            tracker.UpdateFromHeaders(core.Headers);
        }

        using (var search = ResponseWithQuota("search", 30, 29, reset))
        {
            tracker.UpdateFromHeaders(search.Headers);
        }

        tracker.GetQuota("core")!.Limit.ShouldBe(5000);
        tracker.GetQuota("search")!.Limit.ShouldBe(30);
        tracker.GetQuota("graphql").ShouldBeNull();
    }

    [Fact]
    public void UpdateFromHeaders_GraphQLResource_TracksGraphQLBucketSeparately()
    {
        // GraphQL responses carry the same x-ratelimit-* headers as REST but
        // with x-ratelimit-resource: graphql. The tracker must observe these
        // through the same pipeline as REST calls — this test locks in the
        // invariant so regressions show up loudly when refactoring the
        // GraphQL path.
        var tracker = CreateTracker();
        var reset = DateTimeOffset.UtcNow.AddMinutes(60);

        using var response = ResponseWithQuota("graphql", limit: 5000, remaining: 4987, reset);
        tracker.UpdateFromHeaders(response.Headers);

        var quota = tracker.GetQuota("graphql");
        quota.ShouldNotBeNull();
        quota!.Resource.ShouldBe("graphql");
        quota.Limit.ShouldBe(5000);
        quota.Remaining.ShouldBe(4987);

        // REST bucket must be untouched.
        tracker.GetQuota("core").ShouldBeNull();
    }
}