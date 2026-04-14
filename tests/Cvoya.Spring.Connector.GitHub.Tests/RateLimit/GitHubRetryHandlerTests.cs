// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using System.Net;
using System.Net.Http;

using Cvoya.Spring.Connector.GitHub.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class GitHubRetryHandlerTests
{
    private static (GitHubRetryHandler handler, ScriptedHandler inner, FakeTimeProvider time) BuildPipeline(
        IEnumerable<Func<HttpResponseMessage>> responses,
        GitHubRetryOptions? options = null,
        IGitHubRateLimitTracker? tracker = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        options ??= new GitHubRetryOptions
        {
            MaxRetries = 3,
            BaseBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromSeconds(1),
        };
        tracker ??= new GitHubRateLimitTracker(options, NullLoggerFactory.Instance, time);

        var inner = new ScriptedHandler(responses);
        var handler = new GitHubRetryHandler(tracker, options, NullLoggerFactory.Instance, time)
        {
            InnerHandler = inner,
        };

        return (handler, inner, time);
    }

    private static HttpResponseMessage StatusOnly(HttpStatusCode status) => new(status);

    private static HttpResponseMessage RateLimited(HttpStatusCode status, int resetInSeconds, FakeTimeProvider time)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Add("x-ratelimit-limit", "5000");
        response.Headers.Add("x-ratelimit-remaining", "0");
        response.Headers.Add(
            "x-ratelimit-reset",
            time.GetUtcNow().AddSeconds(resetInSeconds).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }

    private static async Task<HttpResponseMessage> InvokeAsync(GitHubRetryHandler handler, FakeTimeProvider time)
    {
        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/test");
        var sendTask = invoker.SendAsync(request, CancellationToken.None);

        // Run the virtual clock forward in small ticks until the task completes,
        // so any Task.Delay(TimeSpan, TimeProvider, ...) scheduled by the handler
        // fires. Cap to prevent runaway tests.
        var pumped = TimeSpan.Zero;
        var step = TimeSpan.FromSeconds(1);
        while (!sendTask.IsCompleted && pumped < TimeSpan.FromMinutes(30))
        {
            time.Advance(step);
            pumped += step;
            // Yield so continuations scheduled on the thread pool get a chance
            // to run before we advance the clock again.
            await Task.Yield();
            await Task.Delay(1);
        }

        return await sendTask;
    }

    [Fact]
    public async Task SendAsync_TwoRateLimitsThenOk_ReturnsOkAfterThreeAttempts()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () => StatusOnly((HttpStatusCode)429),
            () => StatusOnly((HttpStatusCode)429),
            () => StatusOnly(HttpStatusCode.OK),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task SendAsync_ForbiddenPrimaryRateLimit_RetriesRespectingResetHeader()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () => null!, // placeholder, replaced below
            () => StatusOnly(HttpStatusCode.OK),
        });

        // Recreate inner with a time-aware response so the reset header is produced
        // relative to the virtual clock at the moment of the call.
        inner.Reset(new Func<HttpResponseMessage>[]
        {
            () => RateLimited(HttpStatusCode.Forbidden, resetInSeconds: 5, time),
            () => StatusOnly(HttpStatusCode.OK),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task SendAsync_InternalServerError_NotRetried()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () => StatusOnly(HttpStatusCode.InternalServerError),
            () => StatusOnly(HttpStatusCode.OK),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        inner.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_NotFound_NotRetried()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () => StatusOnly(HttpStatusCode.NotFound),
            () => StatusOnly(HttpStatusCode.OK),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inner.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_ForbiddenSecondaryRateLimitWithRetryAfter_Retries()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return r;
            },
            () => StatusOnly(HttpStatusCode.OK),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task SendAsync_ExhaustsRetries_ReturnsLastResponse()
    {
        var (handler, inner, time) = BuildPipeline(new Func<HttpResponseMessage>[]
        {
            () => StatusOnly((HttpStatusCode)429),
            () => StatusOnly((HttpStatusCode)429),
            () => StatusOnly((HttpStatusCode)429),
            () => StatusOnly((HttpStatusCode)429),
        });

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe((HttpStatusCode)429);
        inner.Attempts.ShouldBe(4); // initial + 3 retries
    }

    [Fact]
    public async Task SendAsync_SuccessResponse_UpdatesTracker()
    {
        var options = new GitHubRetryOptions { BaseBackoff = TimeSpan.FromMilliseconds(1), MaxBackoff = TimeSpan.FromSeconds(1) };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var tracker = new GitHubRateLimitTracker(options, NullLoggerFactory.Instance, time);
        var reset = time.GetUtcNow().AddMinutes(30);

        var inner = new ScriptedHandler(new Func<HttpResponseMessage>[]
        {
            () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("x-ratelimit-limit", "5000");
                r.Headers.Add("x-ratelimit-remaining", "4999");
                r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                r.Headers.Add("x-ratelimit-resource", "core");
                return r;
            },
        });

        var handler = new GitHubRetryHandler(tracker, options, NullLoggerFactory.Instance, time)
        {
            InnerHandler = inner,
        };

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var quota = tracker.GetQuota("core");
        quota.ShouldNotBeNull();
        quota!.Remaining.ShouldBe(4999);
    }

    [Fact]
    public async Task SendAsync_GraphQLResponseHeaders_UpdatesGraphQLBucket()
    {
        // GitHub's GraphQL endpoint returns x-ratelimit-resource: graphql.
        // The retry handler sits inside Octokit's HTTP pipeline and must
        // forward those headers to the tracker just like it does for REST.
        var options = new GitHubRetryOptions { BaseBackoff = TimeSpan.FromMilliseconds(1), MaxBackoff = TimeSpan.FromSeconds(1) };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var tracker = new GitHubRateLimitTracker(options, NullLoggerFactory.Instance, time);
        var reset = time.GetUtcNow().AddMinutes(60);

        var inner = new ScriptedHandler(new Func<HttpResponseMessage>[]
        {
            () =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                r.Headers.Add("x-ratelimit-limit", "5000");
                r.Headers.Add("x-ratelimit-remaining", "4321");
                r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
                r.Headers.Add("x-ratelimit-resource", "graphql");
                return r;
            },
        });

        var handler = new GitHubRetryHandler(tracker, options, NullLoggerFactory.Instance, time)
        {
            InnerHandler = inner,
        };

        using var response = await InvokeAsync(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var quota = tracker.GetQuota("graphql");
        quota.ShouldNotBeNull();
        quota!.Remaining.ShouldBe(4321);
        tracker.GetQuota("core").ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_ConcurrentCallers_TrackerConverges()
    {
        var options = new GitHubRetryOptions { BaseBackoff = TimeSpan.FromMilliseconds(1) };
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var tracker = new GitHubRateLimitTracker(options, NullLoggerFactory.Instance, time);
        var reset = time.GetUtcNow().AddMinutes(30);

        var counter = 0;
        var inner = new ScriptedHandler(_ =>
        {
            var n = Interlocked.Increment(ref counter);
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.Add("x-ratelimit-limit", "5000");
            r.Headers.Add("x-ratelimit-remaining", (5000 - n).ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Headers.Add("x-ratelimit-resource", "core");
            return r;
        });

        var handler = new GitHubRetryHandler(tracker, options, NullLoggerFactory.Instance, time)
        {
            InnerHandler = inner,
        };

        var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/test"),
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        counter.ShouldBe(10);
        var quota = tracker.GetQuota("core");
        quota.ShouldNotBeNull();
        quota!.Remaining.ShouldBeInRange(4990, 4999);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private Func<int, HttpResponseMessage> _factory;
        private int _attempts;

        public ScriptedHandler(IEnumerable<Func<HttpResponseMessage>> responses)
        {
            var list = responses.ToList();
            _factory = i => (i < list.Count ? list[i] : list[^1])();
        }

        public ScriptedHandler(Func<int, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        public int Attempts
        {
            get { lock (_lock) { return _attempts; } }
        }

        public void Reset(IEnumerable<Func<HttpResponseMessage>> responses)
        {
            var list = responses.ToList();
            lock (_lock)
            {
                _attempts = 0;
                _factory = i => (i < list.Count ? list[i] : list[^1])();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int n;
            Func<int, HttpResponseMessage> factory;
            lock (_lock)
            {
                n = _attempts++;
                factory = _factory;
            }

            return Task.FromResult(factory(n));
        }
    }
}