// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using Cvoya.Spring.Core.Tenancy;
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
        sut.CurrentTenantId.ShouldBe(OssTenantIds.Default);
    }

    [Fact]
    public void ReturnsConfigured_TenantId()
    {
        var explicitTenant = new Guid("aaaaaaaa-1111-1111-1111-000000000001");
        var sut = new ConfiguredTenantContext(
            Options.Create(new SecretsOptions { DefaultTenantId = explicitTenant }));

        sut.CurrentTenantId.ShouldBe(explicitTenant);
    }

    [Fact]
    public void FallsBack_WhenEmpty()
    {
        var sut = new ConfiguredTenantContext(
            Options.Create(new SecretsOptions { DefaultTenantId = Guid.Empty }));
        sut.CurrentTenantId.ShouldBe(OssTenantIds.Default);
    }
}
