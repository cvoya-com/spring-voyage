// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using System.Reflection;

/// <summary>
/// Detects whether the current process is running under a design-time tool
/// (e.g. <c>dotnet-getdocument</c> for OpenAPI generation, or <c>dotnet-ef</c>
/// for EF Core tooling) rather than as a normal application host.
/// </summary>
/// <remarks>
/// <para>
/// Build-time OpenAPI generation (<c>GenerateOpenApiDocuments</c> MSBuild target)
/// briefly starts the host to scrape endpoint metadata. Infrastructure-dependent
/// hosted services (Dapr Workflow, PersistentAgentRegistry, MCP server,
/// DataProtection, cost trackers) fail loudly because no Dapr sidecar, state
/// store, or Redis exists at build time. This helper lets DI registrations and
/// <c>Program.cs</c> skip those services cleanly. See issue #370.
/// </para>
/// <para>
/// The detection uses the entry assembly name: <c>GetDocument.Insider</c> and
/// <c>dotnet-getdocument</c> for OpenAPI tooling, <c>ef</c> and <c>dotnet-ef</c>
/// for EF Core tooling. The same approach is used by
/// <see cref="ServiceCollectionExtensions"/> for database provider wiring.
/// </para>
/// </remarks>
public static class BuildEnvironment
{
    /// <summary>
    /// <see langword="true"/> when the host is running inside a design-time
    /// tool that loads the assembly for metadata extraction rather than for
    /// serving requests.
    /// </summary>
    public static bool IsDesignTimeTooling { get; } = DetectDesignTimeTooling();

    private static bool DetectDesignTimeTooling()
    {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (entryName is null)
        {
            return false;
        }

        return entryName is "GetDocument.Insider"
            or "dotnet-getdocument"
            or "ef"
            or "dotnet-ef";
    }
}
