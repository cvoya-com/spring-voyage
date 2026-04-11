// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using Cvoya.Spring.Dapr.Prompts;

using FluentAssertions;

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

        result.Should().NotBeNullOrWhiteSpace();
    }
}