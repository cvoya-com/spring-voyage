// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PlatformPromptProvider"/>.
/// </summary>
public class PlatformPromptProviderTests
{
    /// <summary>
    /// Verifies that the platform prompt is non-empty.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_ReturnsNonEmptyPrompt()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Pins the #2679 acceptance: the assembled prompt opens with the
    /// <c>## About Spring Voyage</c> introduction so a runtime arriving
    /// cold has the participant model before the platform contract
    /// refers to "the human or agent who sent the message".
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_OpensWithAboutSpringVoyageIntroduction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldStartWith("## About Spring Voyage");
    }

    /// <summary>
    /// #2679 (with @savasp's comment): the introduction frames units as
    /// a kind of agent that has members, not as a separate primitive.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IntroductionFramesUnitsAsAgentsWithMembers()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("agent that has members");
    }

    /// <summary>
    /// #2679: the introduction names the one-way, message-based
    /// communication model so the contract's later references to
    /// "messages are one-way" land in context.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IntroductionNamesOneWayMessagingModel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("one-way");
        result.ShouldContain("message-based");
    }

    /// <summary>
    /// The non-negotiable contract block is the section that follows the
    /// introduction — both markers must be present and the contract must
    /// appear *after* the introduction.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_PlatformContractBlockFollowsIntroduction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        result.ShouldContain("[END PLATFORM CONTRACT]");

        var introIdx = result.IndexOf("## About Spring Voyage", StringComparison.Ordinal);
        var contractIdx = result.IndexOf("[PLATFORM CONTRACT — NON-NEGOTIABLE]", StringComparison.Ordinal);
        contractIdx.ShouldBeGreaterThan(introIdx);
    }

    /// <summary>
    /// Pins the no-echo guidance — agents must not mirror the
    /// timestamp / sender prefix used in the conversation history into
    /// their replies.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesNoEchoInstruction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Reply with natural-language text only.");
        result.ShouldContain("Do not echo timestamps or sender prefixes");
    }

    /// <summary>
    /// Pins the one-way messaging guidance: domain messaging is one-way,
    /// so the agent must act on an inbound message rather than composing
    /// a reply to a caller that is not waiting on a return value.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesOneWayMessagingModel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Messages on this platform are one-way.");
        result.ShouldContain("do not address your output as if returning a value to a caller");
    }

    /// <summary>
    /// Pins the ADR-0056 clause: stdout is diagnostic; replies are
    /// tool calls. The whole reason the contract exists.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DeclaresStdoutDiagnosticAndRepliesAreToolCalls()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Terminal output (stdout) is captured as a diagnostic reasoning trace only");
        result.ShouldContain("NOT delivered");
        result.ShouldContain("sv.messaging.send");
        result.ShouldContain("RuntimeCompletedSilent");
    }

    /// <summary>
    /// #2681: the reads-vs-side-effects clause names reads as free and
    /// side effects as exclusively tool-mediated.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DeclaresReadsFreeAndSideEffectsViaToolsOnly()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Reads are free");
        result.ShouldContain("side effects");
    }

    /// <summary>
    /// #2681 (with @savasp's comment): communication with humans,
    /// agents, and units happens through <c>sv.messaging.*</c> for v0.1.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EmphasisesMessagingAsTheCommunicationChannel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Communicate with humans, agents, and units through `sv.messaging.*`");
    }

    /// <summary>
    /// #2681 (with @savasp's comment): a concrete non-example shows that
    /// emitting plaintext to stdout is not a reply — the runtime must
    /// invoke the messaging tool.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesHelloNonExample()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("hello back to you");
        result.ShouldContain("MUST call the messaging tool");
    }

    /// <summary>
    /// #2681 (with @savasp's comment): the contract does NOT say "use
    /// only the tools described in this prompt" — tools are discoverable
    /// at runtime via the discovery tools, so the catalog enumerated
    /// here is the always-available core, not a closed set.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DoesNotClaimCatalogIsClosedSet()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("always-available core, not the closed set");
    }

    /// <summary>
    /// Pins the #2670 acceptance: the always-available platform-tool
    /// catalog is named in the platform-instructions section itself, so
    /// an agent reading the prompt cold sees every fundamental-core tool
    /// by name with a one-line purpose — no skill bundle is required to
    /// surface it, and no downstream section should duplicate it.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_NamesEveryFundamentalCoreToolWithOneLinePurpose()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        // The catalog heading is the load-bearing signal that anything
        // following it is the always-available core.
        result.ShouldContain("Platform-tool catalog (always available");

        // ADR-0056 §8 fundamental-core list — every name and nothing
        // extra. Renaming or dropping a tool here must update the ADR
        // first.
        var expected = new[]
        {
            "sv.messaging.send",
            "sv.messaging.multicast",
            "sv.directory.list",
            "sv.directory.lookup",
            "sv.progress.report",
            "sv.tools.list_categories",
            "sv.tools.list",
        };
        foreach (var name in expected)
        {
            result.ShouldContain(name, customMessage: $"core-tool {name} must be named in the platform-instructions section.");
        }
    }

    /// <summary>
    /// Pins the discovery pointer so a runtime that needs a tool
    /// outside the catalog knows how to find one without hallucinating.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_PointsAtCategoryDiscovery()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("sv.tools.list_categories");
        result.ShouldContain("sv.tools.list(<category>)");
    }

    /// <summary>
    /// #2683: the inbound-message envelope section renders after the
    /// platform contract so a runtime arriving cold sees what the
    /// platform actually delivers to its mailbox before the per-subject
    /// sections.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesInboundMessageEnvelopeSection()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("## Inbound messages");

        var contractEndIdx = result.IndexOf("[END PLATFORM CONTRACT]", StringComparison.Ordinal);
        var envelopeIdx = result.IndexOf("## Inbound messages", StringComparison.Ordinal);
        envelopeIdx.ShouldBeGreaterThan(contractEndIdx);
    }

    /// <summary>
    /// #2746/#2747: the envelope section names every field the platform
    /// delivers on an inbound message — from, to, message_id, timestamp,
    /// payload. Renaming a field at the wire boundary requires updating
    /// this list first. <c>thread_id</c> is intentionally NOT present —
    /// the agent never names it (#2747).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopeNamesEveryDeliveredField()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        foreach (var field in new[] { "`from`", "`to`", "`message_id`", "`payload`", "`timestamp`" })
        {
            result.ShouldContain(field, customMessage: $"envelope field {field} must be named in the inbound-messages section.");
        }
    }

    /// <summary>
    /// #2683: the envelope section frames a thread as the participant
    /// set plus the durable message timeline so a runtime understands
    /// what a thread *is* before being told how to inspect one.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopeDefinesThreadConcept()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("set of participants");
        result.ShouldContain("durable timeline");
    }

    /// <summary>
    /// #2683 (per @savasp): the envelope section reinforces that the
    /// messaging tools acknowledge delivery only — they do NOT carry the
    /// recipient's reply. This is the framing that motivates the
    /// sv.thread.* tools (and rules out request/response framing).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopeFramesDeliveryNotReply()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("delivery to the recipient's mailbox");
        result.ShouldContain("do NOT carry the recipient's response");
    }

    /// <summary>
    /// #2747: the envelope section points at the new shared-history surface
    /// (sv.memory.history_with, sv.memory.engagements, sv.memory.search_messages)
    /// and explicitly states that the agent never sees a thread_id — those
    /// were the two affordances the retired sv.thread.* surface provided.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopePointsAtParticipantSetMemoryTools()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("sv.memory.history_with");
        result.ShouldContain("sv.memory.engagements");
        result.ShouldContain("never see a `thread_id`");
        result.ShouldNotContain("sv.thread.");
    }

    /// <summary>
    /// #2746: the envelope is described as a structured shape (bullet
    /// header + JSON appendix) rather than a chat-style turn. This nudges
    /// the runtime away from "answer this turn as text" toward "call the
    /// messaging tool with the right recipients".
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopeIsStructured()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("You received a message.");
        result.ShouldContain("- from:");
        result.ShouldContain("- to:");
        result.ShouldContain("- message_id:");
        result.ShouldContain("- timestamp:");
        result.ShouldContain("- payload:");
        result.ShouldContain("```json");
    }

    /// <summary>
    /// #2747: send takes a recipients list (or scope) and auto-includes
    /// the caller — the agent does not list itself in recipients. The
    /// platform-contract framing must reflect this so a runtime cold-
    /// reading the prompt forms the right tool-call shape on the first try.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_NamesRecipientsListAndAutoInclude()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("recipients");
        result.ShouldContain("auto-include");
    }

    /// <summary>
    /// #2747: send vs multicast are the same input shape but different
    /// thread semantics (shared vs per-pair). The contract must explain
    /// the distinction so the model picks the right tool.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_ContrastsSendVsMulticastThreadSemantics()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("SHARED thread");
        result.ShouldContain("INDEPENDENT 1-1 threads");
    }

    /// <summary>
    /// #2747: connector:// addresses can appear in inbound `from` but are
    /// non-routable as recipients. The prompt names the UnroutableTarget
    /// error so a runtime hitting it has the context to recover.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_ExplainsConnectorRecipientRejection()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("UnroutableTarget");
        result.ShouldContain("non-routable");
    }
}
