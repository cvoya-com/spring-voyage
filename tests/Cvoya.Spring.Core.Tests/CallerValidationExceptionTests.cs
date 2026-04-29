// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="CallerValidationException"/> introduced by #993.
/// The encoding / parsing round-trip is load-bearing because Dapr actor
/// remoting strips custom exception types and only preserves the message
/// text — the router relies on the encoded prefix to recover the
/// classification on the remote side of the hop.
/// </summary>
public class CallerValidationExceptionTests
{
    [Fact]
    public void Constructor_SetsCodeDetailAndEncodesMessage()
    {
        var ex = new CallerValidationException("SOME_CODE", "A detail");

        ex.Code.ShouldBe("SOME_CODE");
        ex.Detail.ShouldBe("A detail");
        ex.Message.ShouldBe("[caller-validation:SOME_CODE] A detail");
    }

    [Fact]
    public void TryParseMessage_RoundTripsEncodedMessage()
    {
        var encoded = new CallerValidationException("MISSING_CONVERSATION_ID", "Domain messages must have a ThreadId").Message;

        var parsed = CallerValidationException.TryParseMessage(encoded, out var code, out var detail);

        parsed.ShouldBeTrue();
        code.ShouldBe("MISSING_CONVERSATION_ID");
        detail.ShouldBe("Domain messages must have a ThreadId");
    }

    [Fact]
    public void TryParseMessage_NonPrefixedMessage_ReturnsFalse()
    {
        var parsed = CallerValidationException.TryParseMessage(
            "Database unavailable", out var code, out var detail);

        parsed.ShouldBeFalse();
        code.ShouldBe(string.Empty);
        detail.ShouldBe(string.Empty);
    }

    [Fact]
    public void TryParseMessage_Null_ReturnsFalse()
    {
        var parsed = CallerValidationException.TryParseMessage(null, out _, out _);

        parsed.ShouldBeFalse();
    }

    [Fact]
    public void TryParseMessage_MalformedPrefix_ReturnsFalse()
    {
        // Missing closing bracket.
        var parsed = CallerValidationException.TryParseMessage(
            "[caller-validation:SOME_CODE detail", out _, out _);

        parsed.ShouldBeFalse();
    }

    [Fact]
    public void InheritsFromSpringException()
    {
        var ex = new CallerValidationException("CODE", "detail");
        ex.ShouldBeAssignableTo<SpringException>();
    }
}