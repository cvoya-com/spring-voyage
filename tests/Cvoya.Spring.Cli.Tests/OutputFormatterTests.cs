// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using Shouldly;

using Xunit;

public class OutputFormatterTests
{
    private static readonly OutputFormatter.Column<AgentResponse>[] AgentColumns =
    {
        new("id", a => a.Id),
        new("name", a => a.Name),
    };

    [Fact]
    public void FormatTable_ArrayData_ProducesAlignedColumns()
    {
        var agents = new[]
        {
            new AgentResponse { Id = "a1", Name = "Alice" },
            new AgentResponse { Id = "b2", Name = "Bob" },
        };

        var result = OutputFormatter.FormatTable(agents, AgentColumns);

        result.ShouldContain("ID");
        result.ShouldContain("NAME");
        result.ShouldContain("a1");
        result.ShouldContain("Alice");
        result.ShouldContain("b2");
        result.ShouldContain("Bob");

        var lines = result.Split(Environment.NewLine);
        lines.Length.ShouldBeGreaterThanOrEqualTo(3);
        lines[1].ShouldMatch(@"^-+\s+-+$");
    }

    [Fact]
    public void FormatTable_SingleObject_TreatedAsOneRowTable()
    {
        var agent = new AgentResponse { Id = "x1", Name = "Xander" };

        var result = OutputFormatter.FormatTable(agent, AgentColumns);

        result.ShouldContain("x1");
        result.ShouldContain("Xander");
    }

    [Fact]
    public void FormatTable_EmptySequence_ReturnsNoResultsMessage()
    {
        var result = OutputFormatter.FormatTable(Array.Empty<AgentResponse>(), AgentColumns);

        result.ShouldBe("No results found.");
    }

    [Fact]
    public void FormatJson_EmitsCamelCaseWireFormat()
    {
        // Kiota's writer emits camelCase property names matching the OpenAPI contract,
        // not the C# PascalCase that System.Text.Json would produce by default.
        var agent = new AgentResponse { Id = "test", Name = "Test", DisplayName = "Test Agent" };

        var result = OutputFormatter.FormatJson(agent);

        var reparsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result);
        reparsed.GetProperty("id").GetString().ShouldBe("test");
        reparsed.GetProperty("displayName").GetString().ShouldBe("Test Agent");
        result.ShouldContain(Environment.NewLine);
    }

    [Fact]
    public void FormatTable_NullProperty_ShowsEmptyString()
    {
        var agents = new[] { new AgentResponse { Id = "a1", Name = null } };

        var result = OutputFormatter.FormatTable(agents, AgentColumns);

        result.ShouldContain("a1");
        result.ShouldContain("NAME");
    }
}