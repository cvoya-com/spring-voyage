// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the free-functions on <see cref="UnitHumansCommand"/>
/// that can be tested without spinning up the System.CommandLine action
/// pipeline (#454).
/// </summary>
public class UnitHumansCommandTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("off", false)]
    [InlineData("none", false)]
    [InlineData("disabled", false)]
    [InlineData("slack,email", true)]
    [InlineData("slack", true)]
    public void ParseNotifications_MapsFreeFormValueToBool(string? input, bool? expected)
    {
        // The `--notifications slack,email` invocation referenced verbatim in
        // docs/guide/observing.md must parse to "notifications enabled" on
        // the wire; channel-specific routing is out of scope for the PATCH
        // contract today (see #454's acceptance criteria).
        UnitHumansCommand.ParseNotifications(input).ShouldBe(expected);
    }
}