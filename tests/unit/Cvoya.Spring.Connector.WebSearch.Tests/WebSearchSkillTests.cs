// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.WebSearch;
using Cvoya.Spring.Connector.WebSearch.Skills;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

public class WebSearchSkillTests
{
    [Fact]
    public async Task ExecuteAsync_ResolvesUnitSecret_AndDispatchesToProvider()
    {
        // Arrange: unit is bound to the 'brave' provider, secret is named
        // 'brave-api-key' and resolves to "super-secret".
        var unitId = new Guid("a1a1a1a1-0000-0000-0000-000000000001");
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var config = new UnitWebSearchConfig("brave", "brave-api-key", 5, true);
        configStore.GetAsync(unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(
                WebSearchConnectorType.WebSearchTypeId,
                JsonSerializer.SerializeToElement(config)));

        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r =>
                    r.Scope == SecretScope.Unit
                    && r.OwnerId == unitId
                    && r.Name == "brave-api-key"),
                Arg.Any<CancellationToken>())
            .Returns(new SecretResolution(
                "super-secret",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Unit, unitId, "brave-api-key")));

        var provider = Substitute.For<IWebSearchProvider>();
        provider.Id.Returns("brave");
        provider.DisplayName.Returns("Brave");
        WebSearchRequest? captured = null;
        provider.SearchAsync(Arg.Do<WebSearchRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new WebSearchResult("Title", "https://example.com", "snippet", null),
            });

        var tenant = Substitute.For<ITenantContext>();
        tenant.CurrentTenantId.Returns(new Guid("c0c0c0c0-0000-0000-0000-000000000001"));

        var scopeFactory = BuildScopeFactory(resolver, tenant);
        var skill = new WebSearchSkill(
            configStore, new[] { provider }, scopeFactory,
            Options.Create(new WebSearchConnectorOptions()),
            NullLoggerFactory.Instance);

        // Act
        var result = await skill.ExecuteAsync(unitId.ToString("N"), "what is spring voyage", limit: null, safesearch: null, TestContext.Current.CancellationToken);

        // Assert
        captured.ShouldNotBeNull();
        captured!.ApiKey.ShouldBe("super-secret");
        captured.Limit.ShouldBe(5);
        captured.Safesearch.ShouldBeTrue();
        result.GetProperty("provider").GetString().ShouldBe("brave");
        result.GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenUnitNotBound()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        configStore.GetAsync("unit-1", Arg.Any<CancellationToken>())
            .Returns((UnitConnectorBinding?)null);

        var skill = new WebSearchSkill(
            configStore,
            Array.Empty<IWebSearchProvider>(),
            BuildScopeFactory(Substitute.For<ISecretResolver>(), Substitute.For<ITenantContext>()),
            Options.Create(new WebSearchConnectorOptions()),
            NullLoggerFactory.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await skill.ExecuteAsync("unit-1", "q", null, null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("not bound");
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsWhenProviderNotRegistered()
    {
        var configStore = Substitute.For<IUnitConnectorConfigStore>();
        var config = new UnitWebSearchConfig("bing", null, 5, true);
        configStore.GetAsync("unit-1", Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(
                WebSearchConnectorType.WebSearchTypeId,
                JsonSerializer.SerializeToElement(config)));

        var skill = new WebSearchSkill(
            configStore,
            Array.Empty<IWebSearchProvider>(),
            BuildScopeFactory(Substitute.For<ISecretResolver>(), Substitute.For<ITenantContext>()),
            Options.Create(new WebSearchConnectorOptions()),
            NullLoggerFactory.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await skill.ExecuteAsync("unit-1", "q", null, null, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("bing");
    }

    private static IServiceScopeFactory BuildScopeFactory(ISecretResolver resolver, ITenantContext tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        services.AddSingleton(tenant);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
