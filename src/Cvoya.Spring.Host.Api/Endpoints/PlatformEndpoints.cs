// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Reflection;

using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Maps read-only platform-metadata endpoints. Feeds the portal's
/// Settings → About panel (#451) and the <c>spring platform info</c>
/// CLI verb; the two surfaces must share one endpoint so they can't
/// drift on version / build-hash reporting.
/// </summary>
public static class PlatformEndpoints
{
    /// <summary>
    /// Registers the platform-info endpoint on <paramref name="app"/>.
    /// </summary>
    public static RouteGroupBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/platform")
            .WithTags("Platform");

        group.MapGet("/info", GetPlatformInfo)
            .WithName("GetPlatformInfo")
            .WithSummary("Get platform version, build hash, and license metadata")
            .Produces<PlatformInfoResponse>(StatusCodes.Status200OK);

        return group;
    }

    private static IResult GetPlatformInfo()
    {
        // The entry assembly is the Host.Api itself. Directory.Build.props
        // pins `<Product>Spring Voyage</Product>`, `<Company>CVOYA LLC</Company>`,
        // and `<PackageLicenseExpression>LicenseRef-BSL-1.1</PackageLicenseExpression>`;
        // those propagate onto the assembly attributes at build time.
        var assembly = Assembly.GetEntryAssembly() ?? typeof(PlatformEndpoints).Assembly;

        // Prefer InformationalVersion (includes the commit suffix when
        // SourceLink is wired) and fall back to the assembly version so
        // local builds without SourceLink still report something useful.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var assemblyVersion = assembly.GetName().Version?.ToString();

        var (version, buildHash) = SplitInformationalVersion(informational, assemblyVersion);

        return Results.Ok(new PlatformInfoResponse(
            Version: version,
            BuildHash: buildHash,
            License: "LicenseRef-BSL-1.1"));
    }

    /// <summary>
    /// InformationalVersion is typically <c>"1.2.3+abcdef0"</c> (SourceLink
    /// appends the commit). Split on '+' to surface the version and the
    /// optional short build hash separately; fall back to the bare
    /// assembly version when no informational attribute is present.
    /// </summary>
    public static (string Version, string? BuildHash) SplitInformationalVersion(
        string? informational,
        string? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            if (plus >= 0)
            {
                var v = informational[..plus];
                var hash = informational[(plus + 1)..];
                return (
                    string.IsNullOrWhiteSpace(v) ? (assemblyVersion ?? "0.0.0") : v,
                    string.IsNullOrWhiteSpace(hash) ? null : Shorten(hash));
            }

            return (informational, null);
        }

        return (assemblyVersion ?? "0.0.0", null);
    }

    private static string Shorten(string hash)
    {
        // Git short hashes are typically 7-10 chars; some CIs embed the full
        // 40-char sha. Clamp so the UI can render it compactly without
        // wrapping. Preserve full hash in JSON? No — the CLI's table view
        // would overflow. Seven chars is the git default.
        return hash.Length <= 12 ? hash : hash[..12];
    }
}