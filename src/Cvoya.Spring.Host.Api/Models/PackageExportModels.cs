// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for <c>POST /api/v1/packages/export</c>.
/// Either <see cref="UnitName"/> or <see cref="InstallId"/> must be supplied
/// — supplying both or neither is a 400 Bad Request.
/// </summary>
public sealed record PackageExportRequest
{
    /// <summary>
    /// The unit (or agent) name as registered in the tenant directory
    /// (e.g. <c>team/architect</c>). Mutually exclusive with
    /// <see cref="InstallId"/>.
    /// </summary>
    public string? UnitName { get; init; }

    /// <summary>
    /// The install batch identifier returned by a prior
    /// <c>POST /api/v1/packages/install</c> call. Mutually exclusive with
    /// <see cref="UnitName"/>.
    /// </summary>
    public Guid? InstallId { get; init; }

    /// <summary>
    /// When <see langword="true"/>, materialises resolved input values into
    /// the <c>inputs:</c> block of the exported YAML. Secret inputs are
    /// exported as placeholder references (<c>${{ secrets.&lt;name&gt; }}</c>),
    /// never as cleartext values.
    ///
    /// When <see langword="false"/> (default), the original <c>inputs:</c>
    /// schema is preserved verbatim (comments and ordering included).
    /// </summary>
    public bool WithValues { get; init; }
}