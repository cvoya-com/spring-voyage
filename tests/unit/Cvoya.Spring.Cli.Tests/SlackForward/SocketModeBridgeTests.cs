// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackForward;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Cli.SlackForward;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the Socket Mode bridge's two stateless utilities — both
/// are on the hot path of the bridge and would silently break the v0
/// signature contract or the slash-command form delivery if they
/// regressed.
/// </summary>
public class SocketModeBridgeTests
{
    [Fact]
    public void ComputeV0Signature_MatchesSpec()
    {
        // The base string the SV signature validator hashes is exactly
        // "v0:{timestamp}:{body}" — same shape Slack publishes in its
        // verifying-requests doc. Replicate the HMAC by hand and the two
        // outputs must match byte-for-byte.
        const string secret = "abcdef-signing-secret";
        const string timestamp = "1700000000";
        const string body = "team_id=T0&command=/sv-help&user_id=U1";

        var expected = ExpectedSignature(secret, timestamp, body);
        var actual = SocketModeBridge.ComputeV0Signature(secret, timestamp, body);

        actual.ShouldBe(expected);
        actual.ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void JsonObjectToForm_RoundtripsSlashCommandShape()
    {
        // Slack delivers slash commands over HTTPS as application/x-www-form-urlencoded.
        // The Socket Mode envelope wraps the same fields in JSON; the bridge has
        // to flatten back to form so the SV endpoint sees the body it expects.
        var payload = JsonDocument.Parse("""
            {
              "team_id": "T0001",
              "command": "/sv-thread",
              "text": "hello world",
              "is_enterprise_install": false
            }
            """).RootElement;

        var form = SocketModeBridge.JsonObjectToForm(payload);

        var parsed = ParseForm(form);
        parsed["team_id"].ShouldBe("T0001");
        parsed["command"].ShouldBe("/sv-thread");
        parsed["text"].ShouldBe("hello world");
        parsed["is_enterprise_install"].ShouldBe("false");
    }

    [Fact]
    public void JsonObjectToForm_EncodesAmpersandsAndEquals()
    {
        // Slash-command text routinely carries `&` and `=` (e.g. shell
        // snippets). Naive concatenation would corrupt the form on the SV
        // side and silently break signature verification once it reached
        // the connector's parser.
        var payload = JsonDocument.Parse("""
            { "command": "/sv-thread", "text": "a&b=c" }
            """).RootElement;

        var form = SocketModeBridge.JsonObjectToForm(payload);
        ParseForm(form)["text"].ShouldBe("a&b=c");
    }

    private static string ExpectedSignature(string secret, string timestamp, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}");
        return System.Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }

    private static System.Collections.Generic.Dictionary<string, string> ParseForm(string form)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var pair in form.Split('&', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
            result[System.Web.HttpUtility.UrlDecode(key)] = System.Web.HttpUtility.UrlDecode(value);
        }
        return result;
    }
}
