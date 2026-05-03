// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System;
using System.IO;
using System.Text.Json;

using A2A.V0_3;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// Pins the wire contract between the agent-sidecar bridge
/// (<c>deployment/agent-sidecar/src/a2a.ts</c>) and the .NET A2A SDK
/// the dispatcher consumes (<c>A2A.V0_3.A2AClient</c>).
///
/// <para>
/// These tests load the JSON fixtures under
/// <c>Execution/Fixtures/</c> — which are regenerated when the bridge
/// wire format changes — and verify that the .NET SDK can deserialize
/// them without throwing, and that
/// <see cref="A2AExecutionDispatcher.MapA2AResponseToMessage"/> maps the
/// result to the expected Spring Voyage message payload.
/// </para>
/// <para>
/// A2A v0.3 wire shape (issue #1198):
/// <list type="bullet">
/// <item>Enum values are kebab-case-lower (<c>"completed"</c>, <c>"agent"</c>)
///   per <c>KebabCaseLowerJsonStringEnumConverter</c>.</item>
/// <item>The <c>message/send</c> result is the flat <see cref="AgentTask"/>
///   with a top-level <c>kind: "task"</c> discriminator — no
///   <c>task</c>/<c>message</c> wrapper.</item>
/// <item><see cref="Part"/> instances carry a <c>kind</c> discriminator
///   (<c>"text"</c>, <c>"file"</c>, or <c>"data"</c>).</item>
/// </list>
/// </para>
/// </summary>
public class BridgeWireContractTests
{
    private static readonly JsonSerializerOptions A2AOptions = A2AJsonUtilities.DefaultOptions;

    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Execution",
            "Fixtures",
            fileName);
        return File.ReadAllText(path);
    }

    private static A2AResponse DeserializeResult(string fixtureJson)
    {
        using var doc = JsonDocument.Parse(fixtureJson);
        var resultElement = doc.RootElement.GetProperty("result");
        var resultJson = resultElement.GetRawText();
        return JsonSerializer.Deserialize<A2AResponse>(resultJson, A2AOptions)
            ?? throw new InvalidOperationException("Deserialization returned null for A2AResponse.");
    }

    private static SvMessage BuildOriginalMessage() =>
        new(
            Id: Guid.NewGuid(),
            From: Address.For("agent", TestSlugIds.HexFor("caller")),
            To: Address.For("agent", TestSlugIds.HexFor("target")),
            Type: MessageType.Domain,
            ThreadId: "thread-1",
            Payload: default,
            Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public void BridgeMessageSendCompleted_DeserializesAsSendMessageResponse_WithCompletedTask()
    {
        // bridge-message-send-completed.json carries kind: "task" at the top
        // level with state: "completed" (kebab-case-lower). The V0_3 SDK
        // uses A2AEventConverterViaKindDiscriminator to resolve the concrete
        // type and KebabCaseLowerJsonStringEnumConverter for TaskState.
        var fixture = LoadFixture("bridge-message-send-completed.json");

        var response = DeserializeResult(fixture);

        response.ShouldBeOfType<AgentTask>();
        var task = (AgentTask)response;
        task.Status.State.ShouldBe(TaskState.Completed);
        task.Id.ShouldBe("stable-id");
        task.ContextId.ShouldBe("stable-contextId");
    }

    [Fact]
    public void BridgeMessageSendCompleted_FlowsThroughDispatcherMapping_ProducesSuccessPayload()
    {
        // Verify end-to-end: fixture → A2AResponse → MapA2AResponseToMessage
        // → Spring Voyage SvMessage with ExitCode: 0 and the artifact text.
        var fixture = LoadFixture("bridge-message-send-completed.json");
        var response = DeserializeResult(fixture);
        var original = BuildOriginalMessage();

        var mapped = A2AExecutionDispatcher.MapA2AResponseToMessage(original, response);

        mapped.ShouldNotBeNull();
        mapped!.Payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
        mapped.Payload.GetProperty("Output").GetString().ShouldBe("echo:hello-from-fixture");
    }

    [Fact]
    public void BridgeMessageSendFailed_DeserializesAsFailedTask_WithAgentRoleStatusMessage()
    {
        // bridge-message-send-failed.json carries kind: "task", state: "failed",
        // and status.message with kind: "message", role: "agent". The SDK must
        // deserialize the nested AgentMessage via its own kind discriminator.
        var fixture = LoadFixture("bridge-message-send-failed.json");

        var response = DeserializeResult(fixture);

        response.ShouldBeOfType<AgentTask>();
        var task = (AgentTask)response;
        task.Status.State.ShouldBe(TaskState.Failed);
        task.Status.Message.ShouldNotBeNull();
        task.Status.Message!.Role.ShouldBe(MessageRole.Agent);
        task.Status.Message.Parts.Count.ShouldBeGreaterThan(0);
        var textPart = task.Status.Message.Parts[0].ShouldBeOfType<TextPart>();
        textPart.Text.ShouldBe("boom");
    }

    [Fact]
    public void BridgeMessageSendFailed_FlowsThroughDispatcherMapping_ProducesErrorPayload()
    {
        // Verify end-to-end: failed fixture → A2AResponse → MapA2AResponseToMessage
        // → Spring Voyage SvMessage with ExitCode: 1 and Error text from the
        // status message and/or artifacts.
        var fixture = LoadFixture("bridge-message-send-failed.json");
        var response = DeserializeResult(fixture);
        var original = BuildOriginalMessage();

        var mapped = A2AExecutionDispatcher.MapA2AResponseToMessage(original, response);

        mapped.ShouldNotBeNull();
        mapped!.Payload.GetProperty("ExitCode").GetInt32().ShouldBe(1);
        // MapA2AResponseToMessage tries artifacts first, then status.message.
        // The fixture includes a "boom" artifact so Output comes from there.
        var output = mapped.Payload.GetProperty("Output").GetString();
        output.ShouldNotBeNullOrEmpty();
        output.ShouldContain("boom");
    }

    [Fact]
    public void BridgeMessageSendCompleted_ResultCarriesKindDiscriminatorAndKebabCaseEnums()
    {
        // Structural assertion: the fixture's result object must carry the
        // v0.3 mandatory fields so consumers can distinguish it from the old
        // proto-style shape without trying to deserialize it.
        var fixture = LoadFixture("bridge-message-send-completed.json");
        using var doc = JsonDocument.Parse(fixture);
        var result = doc.RootElement.GetProperty("result");

        // A2A v0.3: top-level "kind" discriminator.
        result.GetProperty("kind").GetString().ShouldBe("task");
        // Kebab-case-lower enum value.
        result.GetProperty("status").GetProperty("state").GetString().ShouldBe("completed");
        // Part carries its own kind discriminator.
        var partKind = result
            .GetProperty("artifacts")[0]
            .GetProperty("parts")[0]
            .GetProperty("kind")
            .GetString();
        partKind.ShouldBe("text");
    }
}