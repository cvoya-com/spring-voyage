// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SvToolsDiscoverySkillRegistry"/> (ADR-0056 §6 /
/// #2656). Pins: the per-category enumeration, the
/// effective-grant scoping, the "no tool known by name alone" contract
/// (every listing carries the full schema), and the unknown-category
/// behaviour.
/// </summary>
public class SvToolsDiscoverySkillRegistryTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvToolsDiscoverySkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private static ToolCallContext AgentContext(Guid callerId) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: Guid.NewGuid().ToString("N"));

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement;

    private SvToolsDiscoverySkillRegistry CreateRegistry(
        IServiceProvider serviceProvider) =>
        new(
            scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory: _loggerFactory);

    /// <summary>
    /// Wires a DI container with the supplied <paramref name="registries"/>
    /// (plus the discovery registry itself) and an optional
    /// <paramref name="resolver"/> for the grant gate. The DI container is
    /// the production seam — the discovery registry calls
    /// <c>scope.GetServices&lt;ISkillRegistry&gt;()</c> on every
    /// invocation.
    /// </summary>
    private IServiceProvider BuildServiceProvider(
        IEnumerable<ISkillRegistry> registries,
        IToolGrantResolver? resolver = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_loggerFactory);
        foreach (var registry in registries)
        {
            services.AddSingleton(registry);
        }
        if (resolver is not null)
        {
            services.AddSingleton(resolver);
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Name_IsSv()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());
        CreateRegistry(sp).Name.ShouldBe("sv");
    }

    [Fact]
    public void GetToolDefinitions_AdvertisesListCategoriesAndList()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());
        var tools = CreateRegistry(sp).GetToolDefinitions();
        tools.Select(t => t.Name).ShouldBe(
            new[]
            {
                SvToolsDiscoverySkillRegistry.ListCategoriesTool,
                SvToolsDiscoverySkillRegistry.ListTool,
            },
            ignoreOrder: true);
        tools.ShouldAllBe(t => t.Category == ToolCategories.Tools);
    }

    [Fact]
    public async Task NoContextOverload_AlwaysThrows()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());
        var registry = CreateRegistry(sp);
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvToolsDiscoverySkillRegistry.ListCategoriesTool, EmptyArgs(),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());
        var registry = CreateRegistry(sp);
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync(
                "sv.tools.unknown", EmptyArgs(), AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListCategories_NoGrantResolver_ReturnsEveryCategoryWithToolsRegistered()
    {
        // Without an IToolGrantResolver, the discovery registry surfaces
        // the unfiltered set — the limited-test-harness branch.
        var fake = new FakeSkillRegistry(
            new ToolDefinition("messaging.send_demo", "demo", EmptyObjectSchema(), ToolCategories.Messaging),
            new ToolDefinition("directory.lookup_demo", "demo", EmptyObjectSchema(), ToolCategories.Directory),
            new ToolDefinition("uncategorised.tool_demo", "demo", EmptyObjectSchema(), string.Empty));
        var discovery = new SvToolsDiscoverySkillRegistryHarness();
        var sp = BuildServiceProvider(new ISkillRegistry[] { fake, discovery });

        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListCategoriesTool, EmptyArgs(),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var categories = json.GetProperty("categories").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
        categories.ShouldContain(ToolCategories.Messaging);
        categories.ShouldContain(ToolCategories.Directory);
        categories.ShouldContain(ToolCategories.Tools);
        categories.ShouldNotContain(string.Empty,
            "tools with an empty Category must never appear in the categories listing.");
    }

    [Fact]
    public async Task ListCategories_DescriptionsAreNonEmptyForKnownCategories()
    {
        var fake = new FakeSkillRegistry(
            new ToolDefinition("messaging.send_demo", "demo", EmptyObjectSchema(), ToolCategories.Messaging));
        var sp = BuildServiceProvider(new ISkillRegistry[] { fake });

        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListCategoriesTool, EmptyArgs(),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var entry = json.GetProperty("categories").EnumerateArray()
            .First(e => e.GetProperty("name").GetString() == ToolCategories.Messaging);
        entry.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListCategories_GrantsRestrictCategories()
    {
        // The grant resolver only surfaces messaging.send_demo, so the
        // directory category — even though it has registered tools — is
        // hidden from the caller.
        var fake = new FakeSkillRegistry(
            new ToolDefinition("messaging.send_demo", "demo", EmptyObjectSchema(), ToolCategories.Messaging),
            new ToolDefinition("directory.lookup_demo", "demo", EmptyObjectSchema(), ToolCategories.Directory));
        var resolver = Substitute.For<IToolGrantResolver>();
        resolver.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EffectiveTool>>(new[]
            {
                new EffectiveTool("messaging.send_demo", ToolCategories.Messaging, "demo",
                    ToolProvenance.Platform, null),
            }));
        var sp = BuildServiceProvider(new ISkillRegistry[] { fake }, resolver);

        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListCategoriesTool, EmptyArgs(),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var categories = json.GetProperty("categories").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
        categories.ShouldContain(ToolCategories.Messaging);
        categories.ShouldNotContain(ToolCategories.Directory);
    }

    [Fact]
    public async Task List_ReturnsFullSchemasForCategoryTools()
    {
        var schema = JsonDocument.Parse(
            """{ "type": "object", "properties": { "x": { "type": "string" } } }""")
            .RootElement;
        var fake = new FakeSkillRegistry(
            new ToolDefinition("messaging.send_demo", "send a thing", schema, ToolCategories.Messaging));
        var sp = BuildServiceProvider(new ISkillRegistry[] { fake });

        var args = JsonDocument.Parse($$"""{ "category": "{{ToolCategories.Messaging}}" }""").RootElement;
        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListTool, args,
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.GetProperty("category").GetString().ShouldBe(ToolCategories.Messaging);
        json.GetProperty("usage_guidance").GetString().ShouldNotBeNullOrWhiteSpace();
        var tools = json.GetProperty("tools").EnumerateArray().ToList();
        tools.Count.ShouldBe(1);
        tools[0].GetProperty("name").GetString().ShouldBe("messaging.send_demo");
        tools[0].GetProperty("description").GetString().ShouldBe("send a thing");

        // ADR-0056 §6 "no tool known by name alone" — every listing must
        // carry the input schema next to the name.
        tools[0].GetProperty("input_schema").GetProperty("type").GetString().ShouldBe("object");
        tools[0].GetProperty("input_schema").GetProperty("properties").GetProperty("x")
            .GetProperty("type").GetString().ShouldBe("string");
    }

    [Fact]
    public async Task List_UnknownCategory_ReturnsEmptyTools()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());
        var args = JsonDocument.Parse("""{ "category": "not_a_real_category" }""").RootElement;

        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListTool, args,
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.GetProperty("category").GetString().ShouldBe("not_a_real_category");
        json.GetProperty("tools").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task List_MissingCategory_Throws()
    {
        var sp = BuildServiceProvider(Array.Empty<ISkillRegistry>());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await CreateRegistry(sp).InvokeAsync(
                SvToolsDiscoverySkillRegistry.ListTool, EmptyArgs(),
                AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_GrantsExcludeTool_TheToolIsAbsentFromTheListing()
    {
        var fake = new FakeSkillRegistry(
            new ToolDefinition("messaging.send_demo", "demo", EmptyObjectSchema(), ToolCategories.Messaging),
            new ToolDefinition("messaging.other_demo", "demo", EmptyObjectSchema(), ToolCategories.Messaging));
        var resolver = Substitute.For<IToolGrantResolver>();
        resolver.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EffectiveTool>>(new[]
            {
                new EffectiveTool("messaging.send_demo", ToolCategories.Messaging, "demo",
                    ToolProvenance.Platform, null),
            }));
        var sp = BuildServiceProvider(new ISkillRegistry[] { fake }, resolver);

        var args = JsonDocument.Parse($$"""{ "category": "{{ToolCategories.Messaging}}" }""").RootElement;
        var json = await CreateRegistry(sp).InvokeAsync(
            SvToolsDiscoverySkillRegistry.ListTool, args,
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        var names = json.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();
        names.ShouldContain("messaging.send_demo");
        names.ShouldNotContain("messaging.other_demo");
    }

    private static JsonElement EmptyObjectSchema() =>
        JsonDocument.Parse("""{ "type": "object" }""").RootElement;

    /// <summary>
    /// Bare <see cref="ISkillRegistry"/> stub the discovery tests use to
    /// surface arbitrary tool definitions through the registered set.
    /// Invocation is not exercised — discovery only reads
    /// <see cref="ISkillRegistry.GetToolDefinitions"/>.
    /// </summary>
    private sealed class FakeSkillRegistry : ISkillRegistry
    {
        private readonly ToolDefinition[] _tools;
        public FakeSkillRegistry(params ToolDefinition[] tools) => _tools = tools;
        public string Name => "test";
        public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;
        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new SkillNotFoundException(toolName);
    }

    /// <summary>
    /// Marker stub that keeps the discovery registry's own tool
    /// definitions out of the test surface. Some tests deliberately
    /// register fake categories alongside the discovery one — this
    /// stub is the harness analogue when the discovery registry is
    /// expected to appear in the categories list.
    /// </summary>
    private sealed class SvToolsDiscoverySkillRegistryHarness : ISkillRegistry
    {
        public string Name => "sv";
        public IReadOnlyList<ToolDefinition> GetToolDefinitions() =>
        [
            new ToolDefinition(
                SvToolsDiscoverySkillRegistry.ListCategoriesTool,
                "x",
                JsonDocument.Parse("""{ "type": "object" }""").RootElement,
                ToolCategories.Tools),
            new ToolDefinition(
                SvToolsDiscoverySkillRegistry.ListTool,
                "x",
                JsonDocument.Parse("""{ "type": "object" }""").RootElement,
                ToolCategories.Tools),
        ];
        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new SkillNotFoundException(toolName);
    }
}
