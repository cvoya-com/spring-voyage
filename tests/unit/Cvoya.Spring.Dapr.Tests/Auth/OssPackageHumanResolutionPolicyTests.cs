// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Packages;
using Cvoya.Spring.Dapr.Auth;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssPackageHumanResolutionPolicy"/> (ADR-0044
/// § 4 default). Pins the two legitimate branches: caller present ⇒
/// Resolved with the caller's Guid; no caller ⇒ Skipped (for the
/// out-of-request install path).
/// </summary>
public class OssPackageHumanResolutionPolicyTests
{
    private static OssPackageHumanResolutionPolicy CreatePolicy() =>
        new(NullLogger<OssPackageHumanResolutionPolicy>.Instance);

    private static PackageHumanResolutionRequest CreateRequest(Guid? callerHumanId, string role = "owner") =>
        new(
            TenantId: Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001"),
            UnitId: Guid.Parse("bbbbbbbb-2222-2222-2222-000000000001"),
            UnitDisplayName: "test-unit",
            Role: role,
            Expertise: Array.Empty<string>(),
            Notifications: Array.Empty<string>(),
            InstallCallerHumanId: callerHumanId);

    [Fact]
    public async Task ResolveAsync_CallerPresent_ReturnsCallerUuid()
    {
        // OSS default: every package-declared role auto-fills with the
        // install caller's UUID. The operator becomes the human filling
        // every team role declared by the installed package.
        var policy = CreatePolicy();
        var callerId = Guid.Parse("cccccccc-3333-3333-3333-000000000001");

        var resolution = await policy.ResolveAsync(
            CreateRequest(callerHumanId: callerId),
            TestContext.Current.CancellationToken);

        resolution.Outcome.ShouldBe(PackageHumanResolutionOutcome.Resolved);
        resolution.HumanIds.ShouldHaveSingleItem().ShouldBe(callerId);
    }

    [Fact]
    public async Task ResolveAsync_NoCaller_ReturnsSkipped()
    {
        // Out-of-request install path (worker host, background reinstall)
        // surfaces a null caller. OSS treats this as an intentional skip
        // rather than an install failure so platform-internal reinstalls
        // keep working without an operator identity.
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(callerHumanId: null),
            TestContext.Current.CancellationToken);

        resolution.Outcome.ShouldBe(PackageHumanResolutionOutcome.Skipped);
        resolution.HumanIds.ShouldBeEmpty();
        resolution.Reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ResolveAsync_GuidEmptyCaller_ReturnsSkipped()
    {
        // Defensive: an upstream that defaults to Guid.Empty rather than
        // null should still surface as Skipped. The unique-index write
        // path would otherwise land an Empty-Guid row, which is nonsense.
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(callerHumanId: Guid.Empty),
            TestContext.Current.CancellationToken);

        resolution.Outcome.ShouldBe(PackageHumanResolutionOutcome.Skipped);
        resolution.HumanIds.ShouldBeEmpty();
    }
}
