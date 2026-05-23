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
}
