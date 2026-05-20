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
    /// Pins the option-(3) instruction added by #2129 — a system-side
    /// counterweight to the prior-turn formatter change in
    /// <c>ThreadContextBuilder</c>. Weak LLMs were observed mimicking the
    /// prior-turn shape on output (#2089); telling the model up front that
    /// the timestamp / sender prefix is input-only tightens the contract
    /// in addition to scrubbing the leak from the input shape itself.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesNoEchoInstruction()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Reply with natural-language text only.");
        result.ShouldContain("Do not echo the timestamp or sender prefix");
    }

    /// <summary>
    /// Pins the one-way messaging guidance added by ADR-0048: domain
    /// messaging is one-way, so the agent must act on an inbound message
    /// rather than composing a reply to a caller that is not waiting on a
    /// return value.
    /// </summary>
    [Fact]
    public async Task GetPlatformPromptAsync_IncludesOneWayMessagingModel()
    {
        var provider = new PlatformPromptProvider();

        var result = await provider.GetPlatformPromptAsync(TestContext.Current.CancellationToken);

        result.ShouldContain("Messages on this platform are one-way.");
        result.ShouldContain("do not address your output as a reply to a caller");
    }
}
