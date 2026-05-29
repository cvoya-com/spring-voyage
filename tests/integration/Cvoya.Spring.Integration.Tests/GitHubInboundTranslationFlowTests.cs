// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// #2901 — end-to-end coverage that the <b>real</b> GitHub inbound translation
/// (<see cref="GitHubWebhookHandler.TranslatePayload"/>) flows through the unit
/// runtime path and reaches the agent with the real domain payload shape
/// (<c>source</c>/<c>intent</c>/<c>action</c>, snake_case). The sibling
/// <c>GitHubWebhookFlowTests</c> exercise actor forwarding with a synthetic
/// <c>MessageFactory</c> payload (<c>EventType</c>/<c>Action</c>, PascalCase)
/// that the production translator never emits — so a regression in
/// <c>TranslatePayload</c> / <c>BuildIssuePayload</c> would not be caught
/// there. This drives the production translator into the same forwarding path.
/// </summary>
public class GitHubInboundTranslationFlowTests
{
    [Fact]
    public async Task RealIssuesEvent_TranslatedAndRoutedThroughUnit_AgentReceivesRealPayloadShape()
    {
        var (unitActor, _, runtimeInvocationPath, graph) =
            ActorTestHost.CreateUnitActor(actorId: "gh-real-unit");
        graph.SeedAgentMembers(
            TestSlugIds.For("gh-real-unit"), TestSlugIds.For("gh-real-agent"));

        Message? captured = null;
        runtimeInvocationPath
            .InvokeAsync(
                Arg.Any<Address>(),
                Arg.Any<Message>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<Func<ActivityEvent, CancellationToken, Task>?>())
            .Returns(callInfo =>
            {
                captured = callInfo.ArgAt<Message>(1);
                return Task.CompletedTask;
            });

        // A real GitHub `issues`/`opened` webhook payload (GitHub's wire shape).
        var webhookPayload = JsonSerializer.SerializeToElement(new
        {
            action = "opened",
            issue = new
            {
                number = 2901,
                title = "Connector inbound e2e",
                body = "Body",
                labels = Array.Empty<object>(),
                assignee = (object?)null,
                user = new { login = "octocat" },
            },
            repository = new
            {
                name = "spring-voyage",
                full_name = "cvoya-com/spring-voyage",
                owner = new { login = "cvoya-com" },
            },
        });

        // Drive the production translator — NOT MessageFactory's synthetic shape.
        var handler = new GitHubWebhookHandler(NullLoggerFactory.Instance);
        var translated = handler.TranslatePayload("issues", webhookPayload);
        translated.ShouldNotBeNull();
        translated!.From.Scheme.ShouldBe(Address.ConnectorScheme);

        // Address it to the bound unit (production does this in
        // ResolveDestinationsAsync) and route it through the unit runtime path.
        var routed = translated with
        {
            To = Address.For("unit", TestSlugIds.HexFor("gh-real-unit")),
            ThreadId = "gh-real-conv-1",
        };
        await unitActor.ReceiveAsync(routed, TestContext.Current.CancellationToken);

        // The agent's runtime path received the REAL translated payload shape.
        captured.ShouldNotBeNull();
        var payload = captured!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("source").GetString().ShouldBe("github");
        payload.GetProperty("action").GetString().ShouldBe("opened");
        payload.TryGetProperty("intent", out var intent).ShouldBeTrue();
        intent.GetString().ShouldNotBeNullOrWhiteSpace();
        payload.GetProperty("repository").GetProperty("full_name").GetString()
            .ShouldBe("cvoya-com/spring-voyage");

        // And NOT the synthetic MessageFactory shape the false-green test used.
        payload.TryGetProperty("EventType", out _).ShouldBeFalse();
        payload.TryGetProperty("Action", out _).ShouldBeFalse();
    }
}
