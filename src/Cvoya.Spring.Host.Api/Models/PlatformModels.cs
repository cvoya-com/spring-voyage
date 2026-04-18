// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Read-only metadata describing the running Spring Voyage platform.
/// Consumed by the portal's Settings → About panel (PR-S1 Sub-PR D /
/// #451) and the matching <c>spring platform info</c> CLI verb.
/// </summary>
/// <param name="Version">The Spring Voyage assembly version (e.g. <c>1.0.0.0</c>). Never null.</param>
/// <param name="BuildHash">
/// Short git commit hash the running binary was built from, or
/// <c>null</c> when the build did not embed a source-revision id (common
/// for local <c>dotnet run</c> builds).
/// </param>
/// <param name="License">
/// License reference the binary ships under — stable string
/// (<c>LicenseRef-BSL-1.1</c>) rather than the full license body so the
/// UI can key off it without parsing.
/// </param>
public record PlatformInfoResponse(
    string Version,
    string? BuildHash,
    string License);