// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Security.Cryptography;
using System.Text;

using Cvoya.Spring.Connector.Slack.Inbound;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackSignatureValidator"/> per Slack's
/// verifying-requests-from-slack contract: HMAC-SHA256 over
/// <c>v0:&lt;timestamp&gt;:&lt;rawBody&gt;</c> keyed on the binding's
/// <c>signing_secret</c>, with ±5 minute timestamp window.
/// </summary>
public class SlackSignatureValidatorTests
{
    private const string SigningSecret = "8f742231b10e8888abcd99yyyzzz85a5";

    [Fact]
    public void Validate_GoodSignature_GoodTimestamp_ReturnsTrue()
    {
        var sut = new SlackSignatureValidator();
        var rawBody = "{\"type\":\"event_callback\"}";
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = ComputeValidSignature(timestamp, rawBody, SigningSecret);

        sut.Validate(rawBody, timestamp, signature, SigningSecret, now).ShouldBeTrue();
    }

    [Fact]
    public void Validate_TamperedBody_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        var rawBody = "{\"type\":\"event_callback\"}";
        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signature = ComputeValidSignature(timestamp, rawBody, SigningSecret);

        var tampered = "{\"type\":\"event_callback\",\"x\":1}";

        sut.Validate(tampered, timestamp, signature, SigningSecret, now).ShouldBeFalse();
    }

    [Fact]
    public void Validate_StaleTimestamp_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        var rawBody = "{\"type\":\"event_callback\"}";
        var now = DateTimeOffset.UtcNow;

        // 10 minutes ago — outside the ±5 minute window.
        var staleTime = now.AddMinutes(-10);
        var staleTs = staleTime.ToUnixTimeSeconds().ToString();
        var signature = ComputeValidSignature(staleTs, rawBody, SigningSecret);

        sut.Validate(rawBody, staleTs, signature, SigningSecret, now).ShouldBeFalse();
    }

    [Fact]
    public void Validate_FutureTimestampOutsideWindow_ReturnsFalse()
    {
        // Symmetric to stale: 10 minutes in the future is also rejected.
        var sut = new SlackSignatureValidator();
        var rawBody = "{}";
        var now = DateTimeOffset.UtcNow;
        var future = now.AddMinutes(10);
        var futureTs = future.ToUnixTimeSeconds().ToString();
        var signature = ComputeValidSignature(futureTs, rawBody, SigningSecret);

        sut.Validate(rawBody, futureTs, signature, SigningSecret, now).ShouldBeFalse();
    }

    [Fact]
    public void Validate_MissingSignatureHeader_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        var now = DateTimeOffset.UtcNow;
        sut.Validate("{}", now.ToUnixTimeSeconds().ToString(), signature: null, SigningSecret, now)
            .ShouldBeFalse();
        sut.Validate("{}", now.ToUnixTimeSeconds().ToString(), signature: string.Empty, SigningSecret, now)
            .ShouldBeFalse();
    }

    [Fact]
    public void Validate_MissingTimestampHeader_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        sut.Validate("{}", timestamp: null, "v0=" + new string('a', 64), SigningSecret).ShouldBeFalse();
        sut.Validate("{}", timestamp: string.Empty, "v0=" + new string('a', 64), SigningSecret).ShouldBeFalse();
    }

    [Fact]
    public void Validate_MissingPrefix_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var goodSig = ComputeValidSignature(ts, "{}", SigningSecret);
        var noPrefix = goodSig.Replace("v0=", string.Empty, StringComparison.Ordinal);

        sut.Validate("{}", ts, noPrefix, SigningSecret, now).ShouldBeFalse();
    }

    [Fact]
    public void Validate_NonNumericTimestamp_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        sut.Validate("{}", "not-a-number", "v0=abc", SigningSecret).ShouldBeFalse();
    }

    [Fact]
    public void Validate_EmptySigningSecret_ReturnsFalse()
    {
        var sut = new SlackSignatureValidator();
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = ComputeValidSignature(ts, "{}", SigningSecret);
        sut.Validate("{}", ts, sig, string.Empty, now).ShouldBeFalse();
    }

    private static string ComputeValidSignature(string timestamp, string rawBody, string secret)
    {
        var baseString = $"v0:{timestamp}:{rawBody}";
        var key = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(baseString));
        return "v0=" + Convert.ToHexStringLower(hash);
    }
}
