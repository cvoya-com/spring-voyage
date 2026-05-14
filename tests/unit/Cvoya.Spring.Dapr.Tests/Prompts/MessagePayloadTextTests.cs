// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the three payload shapes that flow through the platform to a
/// single extraction contract. Regression coverage for #2230, where the
/// A2A dispatcher's narrower extractor mapped bare-string payloads to the
/// assembled system prompt and leaked it into the user role.
/// </summary>
public class MessagePayloadTextTests
{
    [Fact]
    public void Extract_BareString_ReturnsTheString()
    {
        var payload = JsonSerializer.SerializeToElement("hello world");

        MessagePayloadText.Extract(payload).ShouldBe("hello world");
    }

    [Fact]
    public void Extract_ObjectWithText_ReturnsTextValue()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "wrapped via text" });

        MessagePayloadText.Extract(payload).ShouldBe("wrapped via text");
    }

    [Fact]
    public void Extract_ObjectWithTask_ReturnsTaskValue()
    {
        var payload = JsonSerializer.SerializeToElement(new { Task = "wrapped via Task" });

        MessagePayloadText.Extract(payload).ShouldBe("wrapped via Task");
    }

    [Fact]
    public void Extract_ObjectWithBothTextAndTask_PrefersText()
    {
        var payload = JsonSerializer.SerializeToElement(new { text = "preferred", Task = "fallback" });

        MessagePayloadText.Extract(payload).ShouldBe("preferred");
    }

    [Fact]
    public void Extract_ObjectWithNeitherKey_FallsBackToJsonString()
    {
        var payload = JsonSerializer.SerializeToElement(new { unrelated = 42 });

        MessagePayloadText.Extract(payload).ShouldContain("\"unrelated\":42");
    }

    [Fact]
    public void Extract_NullJsonElement_ReturnsEmptyString()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("null");

        MessagePayloadText.Extract(payload).ShouldBe(string.Empty);
    }

    [Fact]
    public void Extract_DefaultJsonElement_ReturnsEmptyString()
    {
        // A default JsonElement has ValueKind == Undefined; the helper must
        // treat that exactly like Null so callers can safely route through
        // either shape without a guard. (#2230 regression cover.)
        MessagePayloadText.Extract(default).ShouldBe(string.Empty);
    }

    [Fact]
    public void Extract_BareEmptyString_ReturnsEmptyString()
    {
        var payload = JsonSerializer.SerializeToElement(string.Empty);

        MessagePayloadText.Extract(payload).ShouldBe(string.Empty);
    }
}
