// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class ConfiguredTenantContextTests
{
    [Fact]
    public void Defaults_To_Default_WhenNotConfigured()
    {
        var sut = new ConfiguredTenantContext(Options.Create(new SecretsOptions()));
        sut.CurrentTenantId.ShouldBe("default");
    }

    [Theory]
    [InlineData("t1")]
    [InlineData("acme-corp")]
    public void ReturnsConfigured_TenantId(string tenant)
    {
        var sut = new ConfiguredTenantContext(
            Options.Create(new SecretsOptions { DefaultTenantId = tenant }));

        sut.CurrentTenantId.ShouldBe(tenant);
    }

    [Fact]
    public void FallsBack_WhenWhitespace()
    {
        var sut = new ConfiguredTenantContext(
            Options.Create(new SecretsOptions { DefaultTenantId = "   " }));
        sut.CurrentTenantId.ShouldBe("default");
    }
}