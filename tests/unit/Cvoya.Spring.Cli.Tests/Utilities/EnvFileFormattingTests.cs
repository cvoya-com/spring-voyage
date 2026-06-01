// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Utilities;

using Cvoya.Spring.Cli.Utilities;

using Shouldly;

using Xunit;

public class EnvFileFormattingTests
{
    private const string RsaPem =
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEabc\nDEF456\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public void FormatLine_SingleQuotesValueContainingWhitespace()
    {
        // The PEM header carries a literal space ("RSA PRIVATE KEY"); without
        // quoting, `set -a; source` word-splits it and runs "RSA" (#2960).
        var line = EnvFileFormatting.FormatLine("GitHub__PrivateKeyPem", RsaPem);

        line.ShouldBe(
            "GitHub__PrivateKeyPem='-----BEGIN RSA PRIVATE KEY-----\\nMIIEabc\\nDEF456\\n-----END RSA PRIVATE KEY-----'");
    }

    [Fact]
    public void FormatLine_CollapsesRealNewlinesToLiteralBackslashN()
    {
        var line = EnvFileFormatting.FormatLine("K", "a\nb\r\nc");

        // No real newline survives — single-line env-file value.
        line.ShouldNotContain("\n");
        line.ShouldNotContain("\r");
        line.ShouldBe("K='a\\nb\\nc'");
    }

    [Theory]
    // Numeric AppId: MUST stay bare — quotes break the .NET long binder.
    [InlineData("GitHub__AppId", "12345", "GitHub__AppId=12345")]
    // Slug / token / base64-ish / hex: plain tokens, left bare.
    [InlineData("GitHub__AppSlug", "my-app", "GitHub__AppSlug=my-app")]
    [InlineData("GitHub__WebhookSecret", "whsec_aB3-x", "GitHub__WebhookSecret=whsec_aB3-x")]
    [InlineData("K", "QUFBQQ==", "K=QUFBQQ==")]
    [InlineData("K", "deadbeef0123", "K=deadbeef0123")]
    // Plain URL (no shell metacharacters) is safe bare.
    [InlineData(
        "Slack__OAuth__RedirectUri",
        "https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback",
        "Slack__OAuth__RedirectUri=https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback")]
    public void FormatLine_LeavesPlainTokensUnquoted(string key, string value, string expected)
    {
        EnvFileFormatting.FormatLine(key, value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("has space")]       // word-splitting
    [InlineData("has\ttab")]        // word-splitting
    [InlineData("has#hash")]        // comment marker
    [InlineData("has$var")]         // parameter expansion
    [InlineData("has`cmd`")]        // command substitution
    [InlineData("has*glob")]        // pathname expansion
    [InlineData("has;semi")]        // command separator
    public void FormatLine_QuotesValuesWithShellMetacharacters(string value)
    {
        var line = EnvFileFormatting.FormatLine("K", value);

        line.ShouldStartWith("K='");
        line.ShouldEndWith("'");
    }

    [Fact]
    public void FormatLine_EscapesEmbeddedSingleQuote()
    {
        // Defensive: real PEM/base64/hex carry no single quotes, but the helper
        // must still emit a shell-safe line if one ever appears.
        var line = EnvFileFormatting.FormatLine("K", "a'b c");

        // POSIX idiom: close-quote, escaped quote, reopen-quote.
        line.ShouldBe("K='a'\\''b c'");
    }

    [Fact]
    public void FormatLine_EmptyValueIsBareAssignment()
    {
        EnvFileFormatting.FormatLine("REDIS_PASSWORD", string.Empty)
            .ShouldBe("REDIS_PASSWORD=");
    }
}
