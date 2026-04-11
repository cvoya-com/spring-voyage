// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using FluentAssertions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitContextBuilder"/>.
/// </summary>
public class UnitContextBuilderTests
{
    private readonly UnitContextBuilder _builder = new();

    /// <summary>
    /// Verifies that member addresses are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesMemberAddresses()
    {
        var members = new List<Address>
        {
            new("agent", "team/alice"),
            new("agent", "team/bob")
        };

        var result = _builder.Build(members, null, null);

        result.Should().Contain("agent://team/alice");
        result.Should().Contain("agent://team/bob");
        result.Should().Contain("Peer Directory");
    }

    /// <summary>
    /// Verifies that policies are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesPolicies()
    {
        var policies = JsonSerializer.SerializeToElement(new { maxRetries = 3, timeout = "30s" });

        var result = _builder.Build([], policies, null);

        result.Should().Contain("Policies");
        result.Should().Contain("maxRetries");
    }

    /// <summary>
    /// Verifies that skill descriptions are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesSkillDescriptions()
    {
        var skills = new List<Skill>
        {
            new("code-review", "Reviews pull requests", [
                new ToolDefinition("analyze", "Analyzes code changes", JsonSerializer.SerializeToElement(new { }))
            ])
        };

        var result = _builder.Build([], null, skills);

        result.Should().Contain("Available Skills");
        result.Should().Contain("code-review");
        result.Should().Contain("Reviews pull requests");
        result.Should().Contain("analyze");
    }

    /// <summary>
    /// Verifies that empty inputs produce an empty string.
    /// </summary>
    [Fact]
    public void Build_HandlesEmptyInputs()
    {
        var result = _builder.Build([], null, null);

        result.Should().BeEmpty();
    }
}