// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using System.Text.Json;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="ToolRegistry"/> and the
/// <see cref="IToolRegistry"/> contract (#2336 / Sub C of #2332).
/// </summary>
public class ToolRegistryTests
{
    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public void Register_NonCanonicalName_Throws()
    {
        var registry = new ToolRegistry();
        // ToolDefinition's constructor already enforces the canonical
        // pattern from Sub A (#2334); we re-assert that path here so the
        // SDK-contract behaviour is documented end-to-end.
        var ex = Should.Throw<ArgumentException>(
            () => new ToolDefinition(
                Name: "AcmeEcho",
                Description: "bad id",
                InputSchema: EmptyObject()));
        ex.Message.ShouldContain(ToolNaming.Pattern.ToString());
    }

    [Fact]
    public void Register_DuplicateName_Throws()
    {
        var registry = new ToolRegistry();
        var definition = new ToolDefinition(
            "acme.echo", "echoes", EmptyObject());

        registry.Register(definition, static (args, _) => Task.FromResult(args));

        Should.Throw<InvalidOperationException>(() =>
            registry.Register(definition, static (args, _) => Task.FromResult(args)));
    }

    [Fact]
    public void Register_NullDefinition_Throws()
    {
        var registry = new ToolRegistry();
        Should.Throw<ArgumentNullException>(() =>
            registry.Register(null!, static (args, _) => Task.FromResult(args)));
    }

    [Fact]
    public void Register_NullHandler_Throws()
    {
        var registry = new ToolRegistry();
        Should.Throw<ArgumentNullException>(() =>
            registry.Register(
                new ToolDefinition("acme.echo", "x", EmptyObject()),
                null!));
    }

    [Fact]
    public void List_ReturnsRegistrationOrder()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new ToolDefinition("acme.echo", "first", EmptyObject()),
            static (args, _) => Task.FromResult(args));
        registry.Register(
            new ToolDefinition("acme.timestamp", "second", EmptyObject()),
            static (args, _) => Task.FromResult(args));
        registry.Register(
            new ToolDefinition("widget.create_issue", "third", EmptyObject()),
            static (args, _) => Task.FromResult(args));

        var listed = registry.List();
        listed.Select(t => t.Name).ShouldBe(["acme.echo", "acme.timestamp", "widget.create_issue"]);
    }

    [Fact]
    public void List_EmptyRegistry_ReturnsEmpty()
    {
        new ToolRegistry().List().ShouldBeEmpty();
    }

    [Fact]
    public void List_ReturnsDefensiveCopy()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new ToolDefinition("acme.echo", "x", EmptyObject()),
            static (args, _) => Task.FromResult(args));

        var snapshot = registry.List();
        registry.Register(
            new ToolDefinition("acme.timestamp", "y", EmptyObject()),
            static (args, _) => Task.FromResult(args));

        // Snapshot must remain a single-entry list — re-registering must
        // not mutate previous List() returns.
        snapshot.Count.ShouldBe(1);
        registry.List().Count.ShouldBe(2);
    }

    [Fact]
    public void GetHandler_ReturnsRegisteredHandler()
    {
        var registry = new ToolRegistry();
        ToolHandler handler = static (args, _) => Task.FromResult(args);
        registry.Register(
            new ToolDefinition("acme.echo", "x", EmptyObject()),
            handler);

        registry.GetHandler("acme.echo").ShouldBeSameAs(handler);
        registry.GetHandler("acme.timestamp").ShouldBeNull();
        registry.GetHandler(string.Empty).ShouldBeNull();
    }
}
