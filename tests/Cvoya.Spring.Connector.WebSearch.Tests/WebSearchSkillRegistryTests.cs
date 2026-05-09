// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Tests;

using Cvoya.Spring.Connector.WebSearch;
using Cvoya.Spring.Connector.WebSearch.Skills;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

public class WebSearchSkillRegistryTests
{
    [Fact]
    public void Exposes_WebSearch_Tool()
    {
        var registry = Build();

        registry.Name.ShouldBe("web-search");
        var names = registry.GetToolDefinitions().Select(t => t.Name).ToArray();
        names.ShouldContain("webSearch");
    }

    [Fact]
    public async Task Unknown_Tool_Throws_SkillNotFound()
    {
        var registry = Build();

        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("nope", default, TestContext.Current.CancellationToken));
    }

    private static WebSearchSkillRegistry Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ISecretResolver>());
        services.AddSingleton(Substitute.For<ITenantContext>());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var skill = new WebSearchSkill(
            Substitute.For<IUnitConnectorConfigStore>(),
            Array.Empty<IWebSearchProvider>(),
            scopeFactory,
            Options.Create(new WebSearchConnectorOptions()),
            NullLoggerFactory.Instance);
        return new WebSearchSkillRegistry(skill, NullLoggerFactory.Instance);
    }
}
