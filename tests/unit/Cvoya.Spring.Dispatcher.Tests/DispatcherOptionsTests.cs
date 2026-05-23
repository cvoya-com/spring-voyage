// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Sanity tests for <see cref="DispatcherOptions"/>. The previous
/// <c>WorkspaceRoot</c> / <c>DefaultWorkspaceRoot</c> surface was removed
/// in ADR-0055 (pull-based agent bootstrap) — the dispatcher no longer
/// materialises workspace files on the host. This file is retained so the
/// section-name convention stays pinned and any future option additions
/// have a home next to it.
/// </summary>
public class DispatcherOptionsTests
{
    [Fact]
    public void SectionName_IsDispatcher()
    {
        // The configuration section binding rides this constant — flipping
        // it without updating every appsettings.json silently zeroes the
        // bearer-token registry.
        DispatcherOptions.SectionName.ShouldBe("Dispatcher");
    }

    [Fact]
    public void NewInstance_HasNoTokensConfigured()
    {
        new DispatcherOptions().Tokens.ShouldBeEmpty();
    }
}
