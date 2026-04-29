// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ThreadContextBuilder"/>.
/// </summary>
public class ThreadContextBuilderTests
{
    private readonly ThreadContextBuilder _builder = new();

    private static Message CreateMessage(string senderPath, string text)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", senderPath),
            new Address("agent", "receiver"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { text }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that prior messages are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesPriorMessages()
    {
        var messages = new List<Message>
        {
            CreateMessage("team/alice", "Hello there"),
            CreateMessage("team/bob", "Hi Alice")
        };

        var result = _builder.Build(messages, null);

        result.ShouldContain("Prior Messages");
        result.ShouldContain("agent://team/alice");
        result.ShouldContain("Hello there");
        result.ShouldContain("Hi Alice");
    }

    /// <summary>
    /// Verifies that checkpoint state is included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesCheckpointState()
    {
        var result = _builder.Build([], "Step 3 of 5 completed");

        result.ShouldContain("Last Checkpoint");
        result.ShouldContain("Step 3 of 5 completed");
    }

    /// <summary>
    /// Verifies that empty thread produces an empty string.
    /// </summary>
    [Fact]
    public void Build_HandlesEmptyThread()
    {
        var result = _builder.Build([], null);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// Regression — #480 step 2 surfaced this while running the dapr-agent
    /// scenario end-to-end. The CLI `message send` path serialises the user
    /// text as a bare JSON string (UntypedString on the wire); the builder
    /// used to call JsonElement.TryGetProperty on the non-object payload and
    /// crash with InvalidOperationException. ExtractText now accepts every
    /// ValueKind without throwing.
    /// </summary>
    [Fact]
    public void Build_AcceptsBareStringPayload()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "human"),
            new Address("agent", "receiver"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement("Say hello in one sentence."),
            DateTimeOffset.UtcNow);

        var result = _builder.Build([message], null);

        result.ShouldContain("Say hello in one sentence.");
    }

    /// <summary>
    /// The A2A-backed path wraps the message in { Task: "..." }; both shapes
    /// must produce readable history to keep thread context useful.
    /// </summary>
    [Fact]
    public void Build_AcceptsTaskPayloadShape()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "human"),
            new Address("agent", "receiver"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);

        var result = _builder.Build([message], null);

        result.ShouldContain("do-work");
    }
}