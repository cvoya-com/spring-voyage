// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using A2A;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// Pins the wire contract between the agent-sidecar bridge
/// (<c>deployment/agent-sidecar/src/a2a.ts</c>) and the .NET A2A SDK
/// the dispatcher consumes (<c>A2A.A2AClient</c>).
///
/// The fixtures under <c>Execution/Fixtures/</c> are captured verbatim
/// from the bridge's actual JSON-RPC output (see the JS test
/// <c>deployment/agent-sidecar/test/a2a.test.ts</c> for the matching
/// assertions). These tests run the captured JSON through the same
/// <see cref="A2AJsonUtilities.DefaultOptions"/> that
/// <c>A2AClient.SendMessageAsync</c> uses internally and assert that:
///
/// <list type="number">
///   <item>The JSON-RPC <c>result</c> deserializes as
///         <see cref="SendMessageResponse"/> without throwing
///         (regression test for the <c>JsonException</c> at
///         <c>$.task.status.state</c> tracked in #1115).</item>
///   <item>The wrapped <see cref="AgentTask"/> reaches
///         <see cref="A2AExecutionDispatcher.MapA2AResponseToMessage"/>
///         with the right <see cref="TaskState"/> and artifact text.</item>
/// </list>
///
/// If anyone breaks the bridge's wire format again (lowercase enums,
/// drop the <c>task</c> wrapper, …) these fail loudly without needing
/// a real container roundtrip.
/// </summary>
public class BridgeWireContractTests
{
    private static readonly string FixturesRoot = Path.Combine(
        AppContext.BaseDirectory, "Execution", "Fixtures");

    private static SvMessage CreateOriginalMessage()
    {
        return new SvMessage(
            Id: Guid.NewGuid(),
            From: new Address("agent", "sender"),
            To: new Address("agent", "receiver"),
            Type: MessageType.Domain,
            ConversationId: Guid.NewGuid().ToString(),
            Payload: JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static SendMessageResponse DeserializeBridgeResult(string fixtureName)
    {
        var path = Path.Combine(FixturesRoot, fixtureName);
        File.Exists(path).ShouldBeTrue($"missing wire-contract fixture: {path}");
        var envelope = JsonSerializer.Deserialize<JsonElement>(
            File.ReadAllText(path), A2AJsonUtilities.DefaultOptions);
        var resultElement = envelope.GetProperty("result");
        return resultElement.Deserialize<SendMessageResponse>(A2AJsonUtilities.DefaultOptions)
            ?? throw new InvalidOperationException("SendMessageResponse came back null.");
    }

    [Fact]
    public void BridgeMessageSendCompleted_DeserializesAsSendMessageResponse_WithCompletedTask()
    {
        // The bug from #1115: the lowercase A2A 0.3 spec form
        // ("completed") makes the .NET SDK throw a JsonException at
        // $.task.status.state because TaskState is pinned to the
        // proto-style names via [JsonStringEnumMemberName]. With the
        // proto-style "TASK_STATE_COMPLETED" the bridge emits today,
        // this must round-trip cleanly.
        var response = DeserializeBridgeResult("bridge-message-send-completed.json");

        response.PayloadCase.ShouldBe(SendMessageResponseCase.Task);
        response.Task.ShouldNotBeNull();
        response.Task!.Status.ShouldNotBeNull();
        response.Task.Status.State.ShouldBe(TaskState.Completed);
        response.Task.Artifacts.ShouldNotBeNull();
        response.Task.Artifacts!.Count.ShouldBe(1);
        var part = response.Task.Artifacts[0].Parts.ShouldHaveSingleItem();
        part.Text.ShouldBe("echo:hello-from-fixture");
    }

    [Fact]
    public void BridgeMessageSendCompleted_FlowsThroughDispatcherMapping_ProducesSuccessPayload()
    {
        // End-to-end confidence: the bridge's actual output, fed
        // through the dispatcher's response mapper, produces the same
        // success-payload shape the rest of the platform expects.
        var response = DeserializeBridgeResult("bridge-message-send-completed.json");
        var original = CreateOriginalMessage();

        var mapped = A2AExecutionDispatcher.MapA2AResponseToMessage(original, response);

        mapped.ShouldNotBeNull();
        var payload = mapped!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("echo:hello-from-fixture");
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void BridgeMessageSendFailed_DeserializesAsFailedTask_WithAgentRoleStatusMessage()
    {
        // The failure path attaches a status.message with role:
        // ROLE_AGENT and a per-error messageId. Both Role and
        // MessageId are [JsonRequired] on A2A.Message, so a regression
        // that drops either field would surface as a JsonException
        // here too.
        var response = DeserializeBridgeResult("bridge-message-send-failed.json");

        response.PayloadCase.ShouldBe(SendMessageResponseCase.Task);
        response.Task.ShouldNotBeNull();
        response.Task!.Status.State.ShouldBe(TaskState.Failed);
        response.Task.Status.Message.ShouldNotBeNull();
        response.Task.Status.Message!.Role.ShouldBe(Role.Agent);
        response.Task.Status.Message.MessageId.ShouldNotBeNullOrEmpty();
        var part = response.Task.Status.Message.Parts.ShouldHaveSingleItem();
        part.Text.ShouldBe("boom");
    }

    [Fact]
    public void BridgeMessageSendFailed_FlowsThroughDispatcherMapping_ProducesErrorPayload()
    {
        var response = DeserializeBridgeResult("bridge-message-send-failed.json");
        var original = CreateOriginalMessage();

        var mapped = A2AExecutionDispatcher.MapA2AResponseToMessage(original, response);

        mapped.ShouldNotBeNull();
        var payload = mapped!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task BridgeMessageSendCompleted_FlowsThroughA2AClient_WithoutThrowing()
    {
        // The most direct regression: drive the bridge fixture through
        // the actual A2AClient (not just A2AJsonUtilities). This is
        // the same code path the dispatcher hits on every ephemeral
        // turn — without the proto-style enum names the bridge used
        // to emit, this throws JsonException at $.task.status.state.
        var fixturePath = Path.Combine(FixturesRoot, "bridge-message-send-completed.json");
        var bridgeJson = File.ReadAllText(fixturePath);

        using var responder = new FixtureResponder(bridgeJson);
        using var httpClient = new HttpClient(responder, disposeHandler: false);
        var client = new A2AClient(new Uri("http://stub.invalid/"), httpClient);

        var request = new SendMessageRequest
        {
            Message = new A2A.Message
            {
                Role = Role.User,
                Parts = [new Part { Text = "ping" }],
                MessageId = Guid.NewGuid().ToString(),
            },
        };

        var response = await client.SendMessageAsync(request, TestContext.Current.CancellationToken);

        response.PayloadCase.ShouldBe(SendMessageResponseCase.Task);
        response.Task!.Status.State.ShouldBe(TaskState.Completed);
    }

    /// <summary>
    /// Returns the captured bridge JSON verbatim on every POST. The
    /// JSON-RPC id in the response is rewritten to match the request
    /// so <see cref="A2AClient"/>'s id-correlation doesn't reject it.
    /// </summary>
    private sealed class FixtureResponder(string bridgeJson) : HttpMessageHandler
    {
        private readonly string _bridgeJson = bridgeJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? requestId = null;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("id", out var idProp))
                {
                    requestId = idProp.ValueKind == JsonValueKind.String
                        ? JsonSerializer.Serialize(idProp.GetString())
                        : idProp.GetRawText();
                }
            }

            var responseBody = _bridgeJson;
            if (requestId is not null)
            {
                using var doc = JsonDocument.Parse(_bridgeJson);
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == "id")
                        {
                            writer.WritePropertyName("id");
                            writer.WriteRawValue(requestId);
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                responseBody = Encoding.UTF8.GetString(ms.ToArray());
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}