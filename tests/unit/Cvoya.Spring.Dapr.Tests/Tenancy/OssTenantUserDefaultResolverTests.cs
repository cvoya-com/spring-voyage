// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Tenancy;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssTenantUserDefaultResolver"/>. Pins the
/// OSS-default rule: every Human-insert path that doesn't carry an
/// explicit binding stamps the operator pinned at
/// <see cref="OssTenantUserIds.Operator"/> on the new row (ADR-0062 § 1).
/// </summary>
public class OssTenantUserDefaultResolverTests
{
    [Fact]
    public async Task ResolveDefaultAsync_ReturnsOperatorLiteral()
    {
        var resolver = new OssTenantUserDefaultResolver();

        var result = await resolver.ResolveDefaultAsync(TestContext.Current.CancellationToken);

        result.ShouldBe(OssTenantUserIds.Operator);
    }

    [Fact]
    public async Task ResolveDefaultAsync_TwoCalls_ReturnSameValue()
    {
        var resolver = new OssTenantUserDefaultResolver();

        var a = await resolver.ResolveDefaultAsync(TestContext.Current.CancellationToken);
        var b = await resolver.ResolveDefaultAsync(TestContext.Current.CancellationToken);

        a.ShouldBe(b);
        a.ShouldBe(OssTenantUserIds.Operator);
    }
}
