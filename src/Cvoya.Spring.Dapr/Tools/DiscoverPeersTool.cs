// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Tools;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform tool that queries the directory for agents matching a specific role.
/// Returns matching directory entries as a JSON array.
/// </summary>
public class DiscoverPeersTool(
    IDirectoryService directoryService,
    ILoggerFactory loggerFactory) : IPlatformTool
{
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            role = new
            {
                type = "string",
                description = "The role to search for (e.g., 'backend-engineer')."
            }
        },
        required = new[] { "role" },
        additionalProperties = false
    });

    private readonly ILogger _logger = loggerFactory.CreateLogger<DiscoverPeersTool>();

    /// <inheritdoc />
    public string Name => "discoverPeers";

    /// <inheritdoc />
    public string Description => "Query the directory for agents with a specific role.";

    /// <inheritdoc />
    public JsonElement ParametersSchema => Schema;

    /// <inheritdoc />
    public async Task<JsonElement> ExecuteAsync(
        JsonElement parameters,
        JsonElement context,
        CancellationToken cancellationToken = default)
    {
        var role = parameters.GetProperty("role").GetString()
            ?? throw new InvalidOperationException("The 'role' parameter is required.");

        _logger.LogDebug("DiscoverPeers searching for role {Role}", role);

        var entries = await directoryService.ResolveByRoleAsync(role, cancellationToken);

        var results = entries.Select(e => new
        {
            Scheme = e.Address.Scheme,
            Path = e.Address.Path,
            e.ActorId,
            e.DisplayName,
            e.Description,
            e.Role
        });

        return JsonSerializer.SerializeToElement(results);
    }
}
