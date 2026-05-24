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
    /// Pins the platform-contract header — the marker
    /// instruction-tuned models surface as load-bearing so the
    /// platform-mandated clauses below it are treated as non-negotiable.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_OpensWithPlatformContractHeader()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldStartWith("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        result.ShouldContain("[END PLATFORM CONTRACT]");
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

        result.ShouldContain("Terminal output (stdout) is captured as a diagnostic reasoning trace only.");
        result.ShouldContain("NOT delivered");
        result.ShouldContain("sv.messaging.send");
        result.ShouldContain("RuntimeCompletedSilent");
    }

    /// <summary>
    /// Pins the #2670 acceptance: the always-available platform-tool
    /// catalog is named in Layer 1 itself, so an agent reading the
    /// prompt cold sees every fundamental-core tool by name with a
    /// one-line purpose — no skill bundle is required to surface it,
    /// and no downstream layer should duplicate it.
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
            result.ShouldContain(name, customMessage: $"core-tool {name} must be named in Layer 1.");
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
