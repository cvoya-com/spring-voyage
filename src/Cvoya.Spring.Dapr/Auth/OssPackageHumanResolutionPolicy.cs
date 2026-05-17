// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Packages;

using Microsoft.Extensions.Logging;

/// <summary>
/// OSS default <see cref="IPackageHumanResolutionPolicy"/>: every package
/// <c>humans[]</c> declaration auto-fills with the install caller's UUID
/// (ADR-0044 § 4). Out-of-request install paths (worker / background)
/// surface as <see cref="PackageHumanResolutionOutcome.Skipped"/> rather
/// than failing the install — the OSS deployment is single-user, so the
/// only legitimate "no caller" case is a system-internal reinstall, and
/// silently skipping is correct.
/// </summary>
/// <remarks>
/// The cloud overlay pre-registers a hosted variant via the
/// <c>TryAddSingleton</c> seam in <see cref="DependencyInjection.ServiceCollectionExtensionsInfrastructure"/>;
/// that registration wins and this default never runs in the hosted
/// deployment. The policy is a singleton — it consults only the request
/// data, never per-request state.
/// </remarks>
public sealed class OssPackageHumanResolutionPolicy(
    ILogger<OssPackageHumanResolutionPolicy> logger) : IPackageHumanResolutionPolicy
{
    /// <inheritdoc />
    public Task<PackageHumanResolution> ResolveAsync(
        PackageHumanResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.InstallCallerHumanId is { } callerId && callerId != Guid.Empty)
        {
            logger.LogInformation(
                "OssPackageHumanResolutionPolicy: resolving humans[{Role}] on unit '{Unit}' " +
                "to install caller {HumanId}.",
                request.Role, request.UnitDisplayName, callerId);

            return Task.FromResult(new PackageHumanResolution(
                PackageHumanResolutionOutcome.Resolved,
                new[] { callerId }));
        }

        logger.LogInformation(
            "OssPackageHumanResolutionPolicy: no install caller available for humans[{Role}] " +
            "on unit '{Unit}'; skipping the declaration. This is expected for out-of-request " +
            "install paths (worker host, background reinstall).",
            request.Role, request.UnitDisplayName);

        return Task.FromResult(new PackageHumanResolution(
            PackageHumanResolutionOutcome.Skipped,
            Array.Empty<Guid>(),
            Reason: "No install caller available."));
    }
}
