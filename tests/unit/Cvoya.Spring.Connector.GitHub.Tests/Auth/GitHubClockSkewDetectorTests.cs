// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Auth;

using System.Globalization;
using System.Net;

using Cvoya.Spring.Connector.GitHub.Auth;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for #2595 — <see cref="GitHubClockSkewDetector"/> turns a GitHub
/// <c>Date</c> response header plus the local clock into an actionable
/// clock-skew message (or <c>null</c> when the clock is within tolerance).
/// </summary>
public class GitHubClockSkewDetectorTests
{
    private static readonly DateTimeOffset GitHubNow =
        new(2026, 5, 21, 16, 2, 0, TimeSpan.Zero);

    private static string GitHubDateHeader =>
        GitHubNow.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void DescribeSkewIfGross_ClockInSync_ReturnsNull()
    {
        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, GitHubNow);

        result.ShouldBeNull();
    }

    [Fact]
    public void DescribeSkewIfGross_SmallSkewWithinThreshold_ReturnsNull()
    {
        // 30s skew is below the 60s threshold — a genuine credential failure
        // with minor drift must NOT be misreported as a clock problem.
        var localNow = GitHubNow.AddSeconds(-30);

        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, localNow);

        result.ShouldBeNull();
    }

    [Fact]
    public void DescribeSkewIfGross_ContainerBehind_ReturnsActionableMessage()
    {
        // The podman-machine-after-sleep case: container is 6h46m behind.
        var localNow = GitHubNow.Subtract(TimeSpan.FromMinutes((6 * 60) + 46));

        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, localNow);

        result.ShouldNotBeNull();
        result.ShouldContain("behind");
        result.ShouldContain("6 hours 46 minutes");
        result.ShouldContain("Resync");
        result.ShouldContain("credentials themselves are fine");
    }

    [Fact]
    public void DescribeSkewIfGross_ContainerAhead_ReturnsAheadMessage()
    {
        var localNow = GitHubNow.AddMinutes(15);

        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, localNow);

        result.ShouldNotBeNull();
        result.ShouldContain("ahead of");
        result.ShouldContain("15 minutes");
    }

    [Fact]
    public void DescribeSkewIfGross_SkewJustAboveThreshold_ReportsSkew()
    {
        // 90s is just above the 60s threshold — rounds to 2 minutes for
        // display. The point is that a skew barely past the gate is still
        // reported (and reported as a clock problem, not credentials).
        var localNow = GitHubNow.AddSeconds(-90);

        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, localNow);

        result.ShouldNotBeNull();
        result.ShouldContain("2 minutes");
        result.ShouldContain("behind");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void DescribeSkewIfGross_MissingOrUnparseableHeader_ReturnsNull(string? header)
    {
        // No trusted reference time → cannot attribute the failure to the
        // clock; the caller falls back to GitHub's raw rejection.
        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            header, GitHubNow.AddHours(-5));

        result.ShouldBeNull();
    }

    [Fact]
    public void DescribeSkewIfGross_SingularHourAndMinute_FormatsWithoutPluralS()
    {
        var localNow = GitHubNow.Subtract(TimeSpan.FromMinutes(61));

        var result = GitHubClockSkewDetector.DescribeSkewIfGross(
            GitHubDateHeader, localNow);

        result.ShouldNotBeNull();
        result.ShouldContain("1 hour 1 minute");
    }

    [Fact]
    public void TryDetect_GrossSkewWithDateHeader_ReturnsClockSkewException()
    {
        // The end-to-end path: a 401 ("Bad credentials") whose response carries
        // GitHub's trusted Date header, against a container clock 7h behind.
        var rejection = Rejection(GitHubDateHeader);

        var skew = GitHubClockSkewDetector.TryDetect(
            rejection, GitHubNow.AddHours(-7));

        skew.ShouldNotBeNull();
        skew!.Message.ShouldContain("behind");
        skew.Message.ShouldContain("Resync");
        skew.InnerException.ShouldBeSameAs(rejection);
    }

    [Fact]
    public void TryDetect_SmallSkew_ReturnsNull()
    {
        // A real "Bad credentials" failure with an in-sync clock — must NOT be
        // reattributed to the clock.
        var rejection = Rejection(GitHubDateHeader);

        var skew = GitHubClockSkewDetector.TryDetect(rejection, GitHubNow);

        skew.ShouldBeNull();
    }

    [Fact]
    public void TryDetect_NoDateHeader_ReturnsNull()
    {
        // Without a trusted reference time the failure can't be attributed to
        // the clock; the caller falls back to GitHub's raw rejection.
        var rejection = Rejection(dateHeader: null);

        var skew = GitHubClockSkewDetector.TryDetect(
            rejection, GitHubNow.AddHours(-7));

        skew.ShouldBeNull();
    }

    private static ApiException Rejection(string? dateHeader)
    {
        var headers = new Dictionary<string, string>();
        if (dateHeader is not null)
        {
            headers["Date"] = dateHeader;
        }

        return new ApiException(
            new ResponseFake(HttpStatusCode.Unauthorized, headers));
    }

    /// <summary>
    /// Minimal fake of Octokit's <see cref="IResponse"/> exposing a
    /// caller-supplied header set so the tests can drive
    /// <see cref="GitHubClockSkewDetector.TryDetect"/> without an HTTP
    /// round-trip.
    /// </summary>
    private sealed class ResponseFake(
        HttpStatusCode statusCode,
        IReadOnlyDictionary<string, string> headers) : IResponse
    {
        public object Body => string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; } = headers;

        public ApiInfo ApiInfo { get; } = new ApiInfo(
            new Dictionary<string, Uri>(),
            new List<string>(),
            new List<string>(),
            "etag",
            new RateLimit(1, 1, 1));

        public HttpStatusCode StatusCode { get; } = statusCode;

        public string ContentType { get; } = "application/json";
    }
}
