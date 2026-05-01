// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Integration.Tests.TestHelpers;

using global::Dapr.Actors.Runtime;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end integration coverage for mid-flight amendments (#142).
/// Demonstrates a supervisor unit dispatching an amendment to an agent that
/// is already in a live turn, the amendment being queued for the next model
/// call, and the <c>StopAndWait</c> priority breaking the turn out
/// immediately.
/// </summary>
public class AmendmentFlowTests
{
    private const string AgentId = "nudge-agent";
    private const string UnitId = "engineering";

    // Stable UUIDs for tests that need membership lookup via Guid-keyed interface.
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");
    private static readonly Guid AgentNudgeUuid = new("aadaadaa-0000-0000-0000-000000000099");

    /// <summary>
    /// Returns a directory service stub that resolves "engineering" → UnitEngineeringUuid.
    /// Required because amendment sender authorisation resolves slug → UUID (#1492).
    /// </summary>
    private static IDirectoryService BuildDirectoryServiceForEngineering()
    {
        var ds = Substitute.For<IDirectoryService>();
        ds.ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitId),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("unit", UnitId),
                UnitEngineeringUuid.ToString(),
                UnitId, string.Empty, null, DateTimeOffset.UtcNow));
        return ds;
    }

    [Fact]
    public async Task Supervisor_AmendmentMidTurn_IsVisibleToAgentOnNextDispatch()
    {
        // #1492: actor ID must be UUID so Guid.TryParse(Id.GetId()) succeeds
        // in the amendment sender-authorisation path.
        var harness = ActorTestHost.CreateAgentActorWithHarness(
            AgentNudgeUuid.ToString(),
            BuildDirectoryServiceForEngineering());
        harness.MembershipRepository.GetAsync(UnitEngineeringUuid, AgentNudgeUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitEngineeringUuid, AgentNudgeUuid));

        // Simulate an active turn by priming the active-conversation state
        // (the real actor sets this on first domain message; we inline it so
        // the test exercises the "turn in progress" branch of the amendment
        // handler without waiting for an external dispatcher).
        var activeChannel = new ThreadChannel
        {
            ThreadId = "conv-live",
            Messages = new List<Message>
            {
                MessageFactory.CreateDomainMessage(threadId: "conv-live", toId: AgentId),
            },
        };
        harness.StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, activeChannel));

        // The supervisor pushes an amendment mid-turn.
        var amendmentPayload = JsonSerializer.SerializeToElement(new AmendmentPayload(
            Text: "Add a commit message before pushing.",
            Priority: AmendmentPriority.MustRead,
            CorrelationId: "conv-live"));

        var amendment = new Message(
            Guid.NewGuid(),
            new Address("unit", UnitId),
            new Address("agent", AgentId),
            MessageType.Amendment,
            "conv-live",
            amendmentPayload,
            DateTimeOffset.UtcNow);

        var response = await harness.Actor.ReceiveAsync(amendment, TestContext.Current.CancellationToken);
        response.ShouldNotBeNull();

        // The amendment lands on the pending queue so the next dispatch can
        // fold it into the prompt assembly.
        await harness.StateManager.Received().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Is<List<PendingAmendment>>(list =>
                list.Count == 1 &&
                list[0].Text == "Add a commit message before pushing." &&
                list[0].Priority == AmendmentPriority.MustRead),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Supervisor_StopAndWaitAmendment_PausesAgent()
    {
        // #1492: actor ID must be UUID.
        var harness = ActorTestHost.CreateAgentActorWithHarness(
            AgentNudgeUuid.ToString(),
            BuildDirectoryServiceForEngineering());
        harness.MembershipRepository.GetAsync(UnitEngineeringUuid, AgentNudgeUuid, Arg.Any<CancellationToken>())
            .Returns(new UnitMembership(UnitEngineeringUuid, AgentNudgeUuid));

        var payload = JsonSerializer.SerializeToElement(new AmendmentPayload(
            Text: "Stop. Wait for human approval.",
            Priority: AmendmentPriority.StopAndWait,
            CorrelationId: "conv-live"));

        var amendment = new Message(
            Guid.NewGuid(),
            new Address("unit", UnitId),
            new Address("agent", AgentId),
            MessageType.Amendment,
            "conv-live",
            payload,
            DateTimeOffset.UtcNow);

        await harness.Actor.ReceiveAsync(amendment, TestContext.Current.CancellationToken);

        await harness.StateManager.Received().SetStateAsync(
            StateKeys.AgentPaused,
            true,
            Arg.Any<CancellationToken>());
        await harness.StateManager.Received().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Supervisor_NonMemberUnit_AmendmentRejected()
    {
        // #1492: actor ID must be UUID so the authorisation path can parse it.
        var harness = ActorTestHost.CreateAgentActorWithHarness(
            AgentNudgeUuid.ToString(),
            BuildDirectoryServiceForEngineering());
        // No membership row — the agent does not belong to "other-unit".

        var payload = JsonSerializer.SerializeToElement(new AmendmentPayload(
            Text: "ignore-me",
            Priority: AmendmentPriority.Informational,
            CorrelationId: "conv-live"));

        var amendment = new Message(
            Guid.NewGuid(),
            new Address("unit", "other-unit"),
            new Address("agent", AgentId),
            MessageType.Amendment,
            "conv-live",
            payload,
            DateTimeOffset.UtcNow);

        await harness.Actor.ReceiveAsync(amendment, TestContext.Current.CancellationToken);

        await harness.StateManager.DidNotReceive().SetStateAsync(
            StateKeys.AgentPendingAmendments,
            Arg.Any<List<PendingAmendment>>(),
            Arg.Any<CancellationToken>());
    }
}