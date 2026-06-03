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
    /// Pins the #2679 acceptance: the platform-prompt body opens with
    /// the <c>### About Spring Voyage</c> introduction (level updated
    /// per #2738 so it nests under the assembler-emitted
    /// <c>## Platform Instructions</c> parent) so a runtime arriving
    /// cold has the participant model before the platform contract
    /// refers to "the human or agent who sent the message".
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_OpensWithAboutSpringVoyageIntroduction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldStartWith("### About Spring Voyage");
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
    /// The non-negotiable contract block is the sub-section that follows
    /// the introduction. Per #2738 the contract is now a proper
    /// <c>### …</c> heading (no longer the bracketed
    /// <c>[PLATFORM CONTRACT — NON-NEGOTIABLE]</c> marker) so it shows
    /// up in the document outline; the closing
    /// <c>[END PLATFORM CONTRACT]</c> marker is dropped because
    /// markdown section boundaries are implicit. The contract must
    /// still appear *after* the introduction.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_PlatformContractBlockFollowsIntroduction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("### Platform Contract — Non-Negotiable");
        result.ShouldNotContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        result.ShouldNotContain("[END PLATFORM CONTRACT]");

        var introIdx = result.IndexOf("### About Spring Voyage", StringComparison.Ordinal);
        var contractIdx = result.IndexOf("### Platform Contract — Non-Negotiable", StringComparison.Ordinal);
        contractIdx.ShouldBeGreaterThan(introIdx);
    }

    /// <summary>
    /// Pins the no-echo guidance — agents must not mirror the
    /// timestamp / sender prefix used in the conversation history into
    /// the messages they compose. Per #2740 the verb is "respond", not
    /// "reply", so the framing does not suggest swapping the inbound
    /// envelope's `from` / `to` fields blindly.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesNoEchoInstruction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Respond with natural-language text only.");
        result.ShouldContain("Do not echo timestamps or sender prefixes");
    }

    /// <summary>
    /// Pins the one-way messaging guidance: an inbound message is a
    /// notification, not a request awaiting a return value, so the agent
    /// acts on it rather than composing output addressed back at a
    /// caller that is not waiting on a return value (#2740).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesOneWayMessagingModel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("An inbound message is a notification, not a request awaiting a return value");
        result.ShouldContain("do not address your output as if returning a value to a caller");
    }

    /// <summary>
    /// Pins the ADR-0056 clause: stdout is diagnostic; visible actions
    /// are tool calls. The whole reason the contract exists. Per #2739
    /// the prose no longer names the platform-internal
    /// <c>RuntimeCompletedSilent</c> activity (an agent cannot reason
    /// about platform activity names); the clause states what the agent
    /// can act on — a turn that writes only stdout delivers nothing.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DeclaresStdoutDiagnosticAndVisibleActionsAreToolCalls()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Terminal output (stdout) is captured as a diagnostic reasoning trace");
        result.ShouldContain("NOT delivered");
        result.ShouldContain("sv.messaging.send");
        result.ShouldContain("delivers nothing to anyone");
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
    /// #2681 (with @savasp's comment) / #2739: communication with humans,
    /// agents, and units happens through the messaging tools. The
    /// clause names the two tools explicitly rather than the
    /// <c>sv.messaging.*</c> namespace glob (the agent sees a literal
    /// tool list, not a namespace), and names the three valid recipient
    /// kinds so the platform-correct framing lands without referring
    /// the runtime to anywhere else.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EmphasisesMessagingAsTheCommunicationChannel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Send messages to humans, agents, and units through the messaging tools");
        result.ShouldContain("`sv.messaging.send`");
        result.ShouldContain("`sv.messaging.multicast`");
    }

    /// <summary>
    /// #2681 (with @savasp's comment) / #2740: a concrete non-example
    /// shows that emitting plaintext to stdout reaches no one — the
    /// runtime must invoke the messaging tool. The example uses the
    /// real <c>sv.messaging.send</c> shape (a <c>recipients</c> array,
    /// never <c>thread_id</c>) so a runtime cold-reading the prompt
    /// forms the right tool-call shape on the first try (#2739
    /// schema-mismatch fix).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesHelloNonExample()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("hello back to you");
        result.ShouldContain("The tool call is the only way the message is delivered");
        result.ShouldContain("sv.messaging.send(recipients=[\"human:abc123\"]");
    }

    /// <summary>
    /// #2681 (with @savasp's comment): the contract does NOT say "use
    /// only the tools described in this prompt" — tools are discoverable
    /// at runtime via the discovery tools, so the catalog enumerated
    /// here is the core every agent gets, not a closed set.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DoesNotClaimCatalogIsClosedSet()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("core every agent gets, not the closed set");
    }

    /// <summary>
    /// Pins the #2670 acceptance: the platform-tool catalog is named in
    /// the platform-instructions section itself, so an agent reading the
    /// prompt cold sees every fundamental-core tool by name with a
    /// one-line purpose. No downstream section duplicates it. Per #2739
    /// the catalog heading no longer uses the "always available,
    /// regardless of equipped skill bundles" qualifier (an agent has no
    /// model of skill bundles or tool lifetimes — its tool list is
    /// authoritative).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_NamesEveryFundamentalCoreToolWithOneLinePurpose()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        // The catalog heading is the load-bearing signal that anything
        // following it is the core tool surface every agent gets.
        result.ShouldContain("Platform-tool catalog");

        // The in-prompt fundamental-core list (ADR-0056 §8, as amended to
        // promote the full sv.memory.* surface into the core per ADR-0065
        // Decision 3 / audit finding F1). Renaming, dropping, or adding a
        // tool here must update the ADR-0056 §8 amendment first. The
        // durable-store CRUD tools (add/get/list/search/update/delete) and
        // sv.memory.get_messages are no longer left to runtime discovery.
        var expected = new[]
        {
            "sv.messaging.send",
            "sv.messaging.multicast",
            "sv.messaging.respond_to",
            "sv.memory.add",
            "sv.memory.get",
            "sv.memory.list",
            "sv.memory.search",
            "sv.memory.update",
            "sv.memory.delete",
            "sv.memory.engagements",
            "sv.memory.history_with",
            "sv.memory.search_messages",
            "sv.memory.get_messages",
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
    /// Pins ADR-0065 Decision 3 / audit finding F1 (the durable private
    /// CRUD surface was previously unadvertised): the platform prompt now
    /// names every durable-store <c>sv.memory.*</c> tool — the surface an
    /// agent uses to record and recall cross-turn state — so the model does
    /// not have to discover them at runtime (which, in practice, it never
    /// did — the cross-turn memory loss of #2980).
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_AdvertisesDurableMemoryCrudToolSurface()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        foreach (var tool in new[]
        {
            "sv.memory.add",
            "sv.memory.get",
            "sv.memory.list",
            "sv.memory.search",
            "sv.memory.update",
            "sv.memory.delete",
        })
        {
            result.ShouldContain(tool, customMessage: $"durable-memory tool {tool} must be advertised in the platform prompt (ADR-0065 F1).");
        }
    }

    /// <summary>
    /// Pins the durable-memory promotion (per @savasp): the contract does
    /// not merely list the memory tools — it actively tells the runtime to
    /// recall at the start of every turn and record before the end, and
    /// names the concrete tools inline in the durable-memory clause so the
    /// behavioural pointer and the tool surface are co-located.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_ActivelyPromotesDurableMemoryUse()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        // The durable-memory clause names the recall / record tools inline.
        result.ShouldContain("call `sv.memory.search`");
        result.ShouldContain("call `sv.memory.add`");

        // Active-promotion language — habit-forming, not optional.
        result.ShouldContain("make it a habit every turn");
        result.ShouldContain("When in doubt, record it");
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
    /// #2683: the inbound-message envelope sub-section renders after the
    /// platform contract so a runtime arriving cold sees what the
    /// platform actually delivers to its mailbox before the per-subject
    /// sections. Per #2738 the heading is <c>### Inbound messages</c>
    /// so it nests under <c>## Platform Instructions</c>.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesInboundMessageEnvelopeSection()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("### Inbound messages");

        var contractIdx = result.IndexOf("### Platform Contract — Non-Negotiable", StringComparison.Ordinal);
        var envelopeIdx = result.IndexOf("### Inbound messages", StringComparison.Ordinal);
        envelopeIdx.ShouldBeGreaterThan(contractIdx);
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
    /// #2683: the envelope section frames a conversation as the participant
    /// set plus the durable message timeline so a runtime understands what a
    /// conversation *is* before being told how to inspect one. "thread" is an
    /// internal concept and is never exposed as agent-facing vocabulary.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopeDefinesConversationConcept()
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
    /// #2747: the envelope section points at the shared-history surface
    /// (sv.memory.history_with, sv.memory.engagements, sv.memory.search_messages)
    /// and frames conversations by their participant set, not by an internal
    /// id — those were the two affordances the retired sv.thread.* surface
    /// provided. "thread"/"thread_id" is internal and must not surface as
    /// agent-facing vocabulary.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_EnvelopePointsAtParticipantSetMemoryTools()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("sv.memory.history_with");
        result.ShouldContain("sv.memory.engagements");
        result.ShouldContain("Conversations are identified by who is in them");
        result.ShouldNotContain("thread_id");
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
    /// conversation semantics (shared vs per-pair). The contract must explain
    /// the distinction so the model picks the right tool.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_ContrastsSendVsMulticastConversationSemantics()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("SHARED conversation");
        result.ShouldContain("INDEPENDENT 1-1 conversations");
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

    /// <summary>
    /// #2739 acceptance: the platform-contract layer carries no
    /// platform-internal jargon — the agent cannot reason about
    /// platform activity event types, container nouns, or
    /// skill-bundle / equipped vocabulary, so naming them in the
    /// instructions leaves the model with terms it cannot act on.
    /// The terms still appear in operator-facing surfaces (the
    /// activity event-type enum, infrastructure logs, the package
    /// authoring docs) — this test only pins their absence from the
    /// prompt text the platform delivers to a runtime.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DoesNotContainPlatformInternalJargon()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        // Activity event-type names — internal terminology operators
        // see in the activity stream, not actionable for the model.
        result.ShouldNotContain("RuntimeCompletedSilent");
        result.ShouldNotContain("MessageSent activity");

        // The agent does not know it is running in a container — drop
        // the noun where it was used as a platform-internal scope.
        result.ShouldNotContain("this container");

        // Package-authoring vocabulary — the model just sees its tool
        // list, not a notion of "equipped skill bundles".
        result.ShouldNotContain("skill bundle");
        result.ShouldNotContain("Equipped skill");
        result.ShouldNotContain("equipped skill");
    }

    /// <summary>
    /// #2740 acceptance: "reply" is not the messaging-action verb in
    /// the platform contract. Reply suggests blindly inverting the
    /// inbound envelope's <c>from</c> / <c>to</c> fields. The verbs
    /// the contract uses — "send", "compose", "respond", "communicate" —
    /// frame each outbound message as a deliberate choice about whom
    /// to address.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_DoesNotUseReplyAsTheMessagingActionVerb()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        // No "reply" / "replies" anywhere in the prompt text — the
        // word survives only in code comments (XML doc) that describe
        // what was deliberately removed.
        result.ShouldNotContain("reply");
        result.ShouldNotContain("Reply");
        result.ShouldNotContain("replies");
    }

    /// <summary>
    /// #2740 acceptance: the contract names the three routable
    /// recipient kinds explicitly so the agent knows up front that
    /// connector addresses are senders only.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_NamesValidRecipientKindsExplicitly()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Send messages to humans, agents, and units");
        result.ShouldContain("`human:<uuid>`");
        result.ShouldContain("`agent:<uuid>`");
        result.ShouldContain("`unit:<uuid>`");
        result.ShouldContain("Connector addresses (`connector:<uuid>`)");
    }

    /// <summary>
    /// #2739 acceptance: the worked example uses the real
    /// <c>sv.messaging.send</c> shape (the tool takes a
    /// <c>recipients</c> array, not a <c>thread_id</c> argument). A
    /// runtime cold-reading the prompt and copying the example
    /// produces a valid tool call on the first try.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_WorkedExampleUsesRecipientsArrayNotThreadId()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("sv.messaging.send(recipients=[\"human:abc123\"], message=\"hello back to you\")");

        // The pre-#2747 shape — `thread_id=...` and `body=...` — has
        // never matched the wire schema and must not reappear in the
        // worked example.
        result.ShouldNotContain("thread_id=");
        result.ShouldNotContain("body=");
    }

    /// <summary>
    /// #2984 (#3001 / #3008 finding): the contract tells the runtime the
    /// authenticated envelope <c>from</c> is the authoritative sender
    /// identity and that any identity claimed inside message content is
    /// unverified — so agents neither distrust the stamped sender nor
    /// hand-sign their own messages.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_TrustsAuthenticatedEnvelopeFromOverInContentClaims()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Trust the envelope, not the prose.");
        result.ShouldContain("authenticated by the platform from the sender's session");
        result.ShouldContain("it cannot be forged");
        result.ShouldContain("is unverified text");
        result.ShouldContain("do not sign or prefix your own messages");
    }

    /// <summary>
    /// #2984 (keystone of #2980; absorbs #2987 / F1 of #2986): the
    /// contract advertises the durable memory as a behavioural pointer —
    /// recall at turn start, record decisions / completion / ownership
    /// before turn end — and makes a recorded completion authoritative so
    /// agents stop re-requesting delivered artefacts or re-issuing
    /// finished work.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_InstructsDurableMemoryReadRecordAndAuthoritativeCompletion()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Use your durable memory, and treat the message history as ground truth.");
        result.ShouldContain("across turns and across conversations");
        result.ShouldContain("Treat a recorded completion as authoritative");
        result.ShouldContain("do not re-request an artefact already delivered");
    }

    /// <summary>
    /// #2984 (#2993 cheapest self-disavowal fix): the contract points the
    /// runtime at the server-stamped shared message history as ground
    /// truth to consult before acting on an uncertain recollection,
    /// rather than denying or walking back a message it cannot recall.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_TreatsSharedMessageHistoryAsGroundTruth()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("consult the shared message history");
        result.ShouldContain("so you cannot misremember it");
    }
}
