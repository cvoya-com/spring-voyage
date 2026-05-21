// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.Globalization;

using Octokit;

/// <summary>
/// Pure helper that decides whether a GitHub App API rejection is better
/// explained by a skewed local clock than by bad credentials. GitHub stamps
/// every HTTP response — including <c>401</c> rejections — with a <c>Date</c>
/// header; comparing that trusted timestamp against the local clock tells us
/// whether the GitHub App JWT was signed with a clock that is too far off real
/// time for GitHub to accept it.
/// </summary>
/// <remarks>
/// Detection lives in its own helper (rather than inline in the API client) so
/// it is unit-testable without an HTTP round-trip — see
/// <c>GitHubClockSkewDetectorTests</c>.
/// </remarks>
public static class GitHubClockSkewDetector
{
    /// <summary>
    /// Skew (in seconds) above which a <c>Bad credentials</c> rejection is
    /// attributed to the clock rather than the App credentials. GitHub tolerates
    /// only ~60s of drift on the App JWT <c>exp</c>/<c>iat</c> claims; we leave a
    /// margin so genuine credential failures with small/no skew are still
    /// reported as credential failures.
    /// </summary>
    public const int SkewThresholdSeconds = 60;

    /// <summary>
    /// Inspects a GitHub <c>Date</c> response header against the local time and,
    /// when the skew exceeds <see cref="SkewThresholdSeconds"/>, returns an
    /// actionable, developer-facing message explaining the real cause.
    /// </summary>
    /// <param name="gitHubDateHeader">
    /// The raw value of GitHub's <c>Date</c> response header (RFC 1123, e.g.
    /// <c>Tue, 21 May 2026 16:02:00 GMT</c>). May be <c>null</c> / empty when the
    /// response carried no usable header — in which case no skew is reported.
    /// </param>
    /// <param name="localNow">The current local (container) time.</param>
    /// <returns>
    /// An actionable message when gross skew is detected; <c>null</c> when the
    /// clock is within tolerance or the header could not be parsed.
    /// </returns>
    public static string? DescribeSkewIfGross(string? gitHubDateHeader, DateTimeOffset localNow)
    {
        if (!TryParseGitHubDate(gitHubDateHeader, out var gitHubNow))
        {
            return null;
        }

        var skew = localNow - gitHubNow;
        if (Math.Abs(skew.TotalSeconds) <= SkewThresholdSeconds)
        {
            return null;
        }

        // skew > 0 → local clock is AHEAD of GitHub; skew < 0 → BEHIND.
        // The podman-machine-sleep case is "behind", but the message handles
        // either direction so a fast clock is diagnosed just as clearly.
        var direction = skew.TotalSeconds < 0 ? "behind" : "ahead of";
        var magnitude = FormatMagnitude(skew.Duration());

        return
            $"The container clock appears to be {magnitude} {direction} real time " +
            $"(GitHub's clock vs. this container's clock). GitHub rejected the " +
            $"GitHub App JWT as \"Bad credentials\" because the token's expiry was " +
            $"computed from the skewed clock — the credentials themselves are fine. " +
            $"Resync the container/host clock and retry. On macOS/Windows the " +
            $"podman-machine VM clock freezes during host sleep; resync it with " +
            $"`podman machine ssh 'sudo chronyc makestep'`, then re-run the wizard.";
    }

    /// <summary>
    /// Examines a GitHub <c>401</c> rejection for gross clock skew and, when
    /// found, returns a <see cref="GitHubClockSkewException"/> carrying an
    /// actionable message; returns <c>null</c> when the rejection is not
    /// attributable to the clock (small/no skew, or no usable <c>Date</c>
    /// header on the response).
    /// </summary>
    /// <param name="rejection">
    /// The Octokit exception raised for a <c>401 Bad credentials</c> from an
    /// App-JWT-authenticated call.
    /// </param>
    /// <param name="localNow">The current local (container) time.</param>
    /// <returns>
    /// A ready-to-throw <see cref="GitHubClockSkewException"/>, or <c>null</c>
    /// when the failure is not a clock-skew failure.
    /// </returns>
    public static GitHubClockSkewException? TryDetect(
        ApiException rejection, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        string? dateHeader = null;
        if (rejection.HttpResponse?.Headers is { } headers
            && headers.TryGetValue("Date", out var headerValue))
        {
            dateHeader = headerValue;
        }

        var skewMessage = DescribeSkewIfGross(dateHeader, localNow);
        return skewMessage is null
            ? null
            : new GitHubClockSkewException(skewMessage, rejection);
    }

    private static bool TryParseGitHubDate(string? header, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        // GitHub's Date header is RFC 1123 ("R"). DateTimeOffset.TryParse with
        // RoundtripKind handles the trailing "GMT" and yields a UTC offset.
        return DateTimeOffset.TryParse(
            header,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
    }

    private static string FormatMagnitude(TimeSpan skew)
    {
        if (skew.TotalMinutes < 1)
        {
            var seconds = (int)Math.Round(skew.TotalSeconds);
            return $"{seconds} second{(seconds == 1 ? string.Empty : "s")}";
        }

        if (skew.TotalHours < 1)
        {
            var minutes = (int)Math.Round(skew.TotalMinutes);
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        var hours = (int)skew.TotalHours;
        var remMinutes = skew.Minutes;
        var hourPart = $"{hours} hour{(hours == 1 ? string.Empty : "s")}";
        return remMinutes == 0
            ? hourPart
            : $"{hourPart} {remMinutes} minute{(remMinutes == 1 ? string.Empty : "s")}";
    }
}
