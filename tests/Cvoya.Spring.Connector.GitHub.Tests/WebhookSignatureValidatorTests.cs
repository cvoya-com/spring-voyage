// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Security.Cryptography;
using System.Text;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using FluentAssertions;
using Xunit;

public class WebhookSignatureValidatorTests
{
    private const string Secret = "test-webhook-secret";
    private const string Payload = """{"action":"opened","issue":{"number":1}}""";

    [Fact]
    public void Validate_ValidSignature_ReturnsTrue()
    {
        var signature = ComputeSignature(Payload, Secret);

        var result = WebhookSignatureValidator.Validate(Payload, signature, Secret);

        result.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidSignature_ReturnsFalse()
    {
        var result = WebhookSignatureValidator.Validate(Payload, "sha256=invalid", Secret);

        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptySecret_ReturnsFalse()
    {
        var result = WebhookSignatureValidator.Validate(Payload, "sha256=abc", string.Empty);

        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyPayload_ReturnsFalse()
    {
        var result = WebhookSignatureValidator.Validate(string.Empty, "sha256=abc", Secret);

        result.Should().BeFalse();
    }

    [Fact]
    public void Validate_MissingPrefix_ReturnsFalse()
    {
        var result = WebhookSignatureValidator.Validate(Payload, "nothash=abc", Secret);

        result.Should().BeFalse();
    }

    private static string ComputeSignature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
