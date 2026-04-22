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

    [Fact]
    public void FormatJson_KiotaWriterThrows_FallsBackToSystemTextJson()
    {
        // #1064: the bundled Kiota JSON writer trips on certain Untyped*
        // payloads with `'}' is invalid following a property name`. The
        // formatter must fall back to System.Text.Json so `--output json`
        // never crashes a CLI invocation. We force the failure with a
        // model whose Serialize method writes a property name without a
        // value — the same shape the real Utf8JsonWriter rejects.
        var faulty = new FaultyKiotaModel { Id = "test" };

        var result = OutputFormatter.FormatJson(faulty);

        // Fallback uses STJ with camelCase + indented options. The
        // resulting JSON should be parseable and carry the model's Id.
        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(result);
        parsed.GetProperty("id").GetString().ShouldBe("test");
    }

    [Fact]
    public void FormatJson_KiotaWriterThrows_VerboseEmitsWarningToStderr()
    {
        // The fallback should be silent by default (don't pollute scripted
        // output) but emit a one-line warning when --verbose is passed so
        // operators know why the wire-format may differ.
        var originalStderr = Console.Error;
        try
        {
            using var stderr = new System.IO.StringWriter();
            Console.SetError(stderr);

            var faulty = new FaultyKiotaModel { Id = "x" };
            _ = OutputFormatter.FormatJson(faulty, verbose: true);

            stderr.ToString().ShouldContain("kiota serializer failed");
            stderr.ToString().ShouldContain("System.Text.Json");
        }
        finally
        {
            Console.SetError(originalStderr);
        }
    }

    [Fact]
    public void FormatJson_KiotaWriterThrows_DefaultModeIsSilent()
    {
        var originalStderr = Console.Error;
        try
        {
            using var stderr = new System.IO.StringWriter();
            Console.SetError(stderr);

            var faulty = new FaultyKiotaModel { Id = "x" };
            _ = OutputFormatter.FormatJson(faulty);

            stderr.ToString().ShouldBeEmpty();
        }
        finally
        {
            Console.SetError(originalStderr);
        }
    }

    /// <summary>
    /// Stands in for the Kiota model that triggered #1064 — directly
    /// throws <see cref="InvalidOperationException"/> from
    /// <c>Serialize</c> with the same message the real
    /// <c>Utf8JsonWriter.WriteEndObject</c> validation throws. The
    /// formatter only cares about catching the exception type and
    /// falling back to STJ; the precise root cause inside Kiota is the
    /// subject of #1064's investigation, not the CLI fix.
    /// </summary>
    private sealed class FaultyKiotaModel : Microsoft.Kiota.Abstractions.Serialization.IParsable
    {
        public string? Id { get; set; }

        public IDictionary<string, Action<Microsoft.Kiota.Abstractions.Serialization.IParseNode>> GetFieldDeserializers()
            => new Dictionary<string, Action<Microsoft.Kiota.Abstractions.Serialization.IParseNode>>();

        public void Serialize(Microsoft.Kiota.Abstractions.Serialization.ISerializationWriter writer)
        {
            // Same exception type and message the Kiota JsonSerializationWriter
            // surfaces when the underlying Utf8JsonWriter rejects a
            // half-written object in production (see #1064 stack trace).
            throw new InvalidOperationException("'}' is invalid following a property name.");
        }
    }
}