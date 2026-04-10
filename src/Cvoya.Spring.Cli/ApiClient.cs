// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// HTTP client that communicates with the Spring API.
/// </summary>
public class SpringApiClient(HttpClient httpClient)
{
    // Agents

    /// <summary>
    /// Lists all agents.
    /// </summary>
    public async Task<JsonElement> ListAgentsAsync(CancellationToken ct = default)
    {
        return await GetJsonAsync("/api/v1/agents", ct);
    }

    /// <summary>
    /// Creates a new agent.
    /// </summary>
    public async Task<JsonElement> CreateAgentAsync(string id, string name, string? role, CancellationToken ct = default)
    {
        var payload = new { id, name, role };
        return await PostJsonAsync("/api/v1/agents", payload, ct);
    }

    /// <summary>
    /// Gets the status of an agent.
    /// </summary>
    public async Task<JsonElement> GetAgentStatusAsync(string id, CancellationToken ct = default)
    {
        return await GetJsonAsync($"/api/v1/agents/{Uri.EscapeDataString(id)}", ct);
    }

    /// <summary>
    /// Deletes an agent.
    /// </summary>
    public async Task DeleteAgentAsync(string id, CancellationToken ct = default)
    {
        var response = await httpClient.DeleteAsync($"/api/v1/agents/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    // Units

    /// <summary>
    /// Lists all units.
    /// </summary>
    public async Task<JsonElement> ListUnitsAsync(CancellationToken ct = default)
    {
        return await GetJsonAsync("/api/v1/units", ct);
    }

    /// <summary>
    /// Creates a new unit.
    /// </summary>
    public async Task<JsonElement> CreateUnitAsync(string id, string name, CancellationToken ct = default)
    {
        var payload = new { id, name };
        return await PostJsonAsync("/api/v1/units", payload, ct);
    }

    /// <summary>
    /// Deletes a unit.
    /// </summary>
    public async Task DeleteUnitAsync(string id, CancellationToken ct = default)
    {
        var response = await httpClient.DeleteAsync($"/api/v1/units/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Adds a member to a unit.
    /// </summary>
    public async Task AddMemberAsync(string unitId, string memberScheme, string memberPath, CancellationToken ct = default)
    {
        var payload = new { scheme = memberScheme, path = memberPath };
        await PostJsonAsync($"/api/v1/units/{Uri.EscapeDataString(unitId)}/members", payload, ct);
    }

    /// <summary>
    /// Removes a member from a unit.
    /// </summary>
    public async Task RemoveMemberAsync(string unitId, string memberId, CancellationToken ct = default)
    {
        var response = await httpClient.DeleteAsync(
            $"/api/v1/units/{Uri.EscapeDataString(unitId)}/members/{Uri.EscapeDataString(memberId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    // Messages

    /// <summary>
    /// Sends a message to an address.
    /// </summary>
    public async Task<JsonElement> SendMessageAsync(string toScheme, string toPath, string text, string? conversationId, CancellationToken ct = default)
    {
        var payload = new
        {
            to = new { scheme = toScheme, path = toPath },
            text,
            conversationId
        };
        return await PostJsonAsync("/api/v1/messages", payload, ct);
    }

    // Directory

    /// <summary>
    /// Lists all directory entries.
    /// </summary>
    public async Task<JsonElement> ListDirectoryAsync(CancellationToken ct = default)
    {
        return await GetJsonAsync("/api/v1/directory", ct);
    }

    // Auth tokens

    /// <summary>
    /// Creates a new API token.
    /// </summary>
    public async Task<JsonElement> CreateTokenAsync(string name, CancellationToken ct = default)
    {
        var payload = new { name };
        return await PostJsonAsync("/api/v1/auth/tokens", payload, ct);
    }

    /// <summary>
    /// Lists all API tokens.
    /// </summary>
    public async Task<JsonElement> ListTokensAsync(CancellationToken ct = default)
    {
        return await GetJsonAsync("/api/v1/auth/tokens", ct);
    }

    /// <summary>
    /// Revokes an API token by name.
    /// </summary>
    public async Task RevokeTokenAsync(string name, CancellationToken ct = default)
    {
        var response = await httpClient.DeleteAsync($"/api/v1/auth/tokens/{Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var response = await httpClient.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task<JsonElement> PostJsonAsync(string path, object payload, CancellationToken ct)
    {
        var response = await httpClient.PostAsJsonAsync(path, payload, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
}
