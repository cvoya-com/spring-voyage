// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the JSON-RPC wire shape <see cref="MessagingClient"/> emits for the
/// platform messaging tools. The pre-#2747 client sent a singular
/// <c>address</c> / <c>addresses</c>; the live contract takes
/// <c>recipients[]</c> for both <c>sv.messaging.send</c> and
/// <c>sv.messaging.multicast</c> (#2747 / #2889). The old shape would fail
/// silently at the tool boundary — the receiver parses <c>recipients</c> —
/// and was untested, so these tests guard against the regression returning.
/// </summary>
public class MessagingClientTests
{
    private sealed class CapturingHandler(string resultJson) : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // Minimal MCP tools/call success envelope: result.content[].text
            // carries the JSON-encoded tool acknowledgement.
            var envelope = new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new[] { new { type = "text", text = resultJson } },
                },
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static MessagingClient ClientWith(CapturingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://mcp.local/") }, "test-token");

    private static JsonElement ArgumentsOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("params").GetProperty("arguments").Clone();
    }

    [Fact]
    public async Task SendAsync_EmitsRecipientsArray_NotSingularAddress()
    {
        var handler = new CapturingHandler(
            "{\"delivered\":true,\"messageId\":\"m-1\",\"target\":\"unit:abc\",\"threadId\":\"t-1\"}");
        var client = ClientWith(handler);

        await client.SendAsync(
            "t-1", "11111111111111111111111111111111", "hi", CancellationToken.None);

        handler.CapturedBody.ShouldNotBeNull();
        var args = ArgumentsOf(handler.CapturedBody!);
        args.TryGetProperty("recipients", out var recipients).ShouldBeTrue();
        recipients.ValueKind.ShouldBe(JsonValueKind.Array);
        recipients.GetArrayLength().ShouldBe(1);
        // The retired singular field must NOT appear.
        args.TryGetProperty("address", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task MulticastAsync_EmitsRecipientsArray_NotAddresses()
    {
        var handler = new CapturingHandler(
            "{\"messageId\":\"m-1\",\"threadId\":\"t-1\",\"deliveries\":[]}");
        var client = ClientWith(handler);

        await client.MulticastAsync(
            "t-1",
            new[]
            {
                "11111111111111111111111111111111",
                "22222222222222222222222222222222",
            },
            "hi",
            CancellationToken.None);

        handler.CapturedBody.ShouldNotBeNull();
        var args = ArgumentsOf(handler.CapturedBody!);
        args.TryGetProperty("recipients", out var recipients).ShouldBeTrue();
        recipients.ValueKind.ShouldBe(JsonValueKind.Array);
        recipients.GetArrayLength().ShouldBe(2);
        // The retired plural field must NOT appear.
        args.TryGetProperty("addresses", out _).ShouldBeFalse();
    }
}
