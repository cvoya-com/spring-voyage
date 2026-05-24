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
}
