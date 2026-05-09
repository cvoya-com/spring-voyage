// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Adapts the expertise-directory-driven skill catalog (#359) to the
/// <see cref="ISkillRegistry"/> extension seam so the skill surface is
/// discoverable by the existing MCP server, the <c>/api/v1/skills</c>
/// endpoint, and any future planner — without any of those learning about
/// the expertise directory directly.
/// </summary>
/// <remarks>
/// <para>
/// Registered via <see cref="ISkillRegistry"/> with <c>TryAdd*</c> semantics
/// (one element of the enumerable — see <c>AddCvoyaSpringDapr</c>) so
/// downstream hosts that want to replace the catalog (test doubles,
/// tenant-scoped filters) can register their own implementation before
/// calling the OSS bootstrapper. MCP tool dispatch for registry entries
/// still flows through <see cref="ISkillInvoker"/>, which preserves the
/// <see cref="IMessageRouter"/> governance chain.
/// </para>
/// <para>
/// <b>Live enumeration.</b> <see cref="GetToolDefinitions"/> currently
/// returns the last-enumerated snapshot and refreshes it on the next
/// invocation — the <see cref="ISkillRegistry"/> interface is synchronous
/// by design, so we cannot await the catalog from it. Call sites that need
/// the freshest surface (the MCP server, the /skills endpoint) invoke
/// <see cref="InvokeAsync"/>, which re-resolves live via the catalog. The
/// eager behaviour does mean a brand-new expertise entry shows up on
/// <c>GetToolDefinitions()</c> one call after the mutation, not instantly;
/// <c>tools/list</c> over MCP only happens at session initialization per
/// agent, so this is not user-visible in practice.
/// </para>
/// </remarks>
public class ExpertiseSkillRegistry(
    IExpertiseSkillCatalog catalog,
    ISkillInvoker invoker,
    ILoggerFactory loggerFactory) : ISkillRegistry
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ExpertiseSkillRegistry>();
    private IReadOnlyList<ToolDefinition> _lastTools = Array.Empty<ToolDefinition>();

    /// <inheritdoc />
    public string Name => "expertise";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        // Kick off an async refresh and return the most recent snapshot.
        // MCP's tools/list happens on session initialization, so a
        // one-roundtrip lag on a mutation burst is acceptable; correctness
        // is re-checked at InvokeAsync through the catalog resolver.
        _ = RefreshToolsAsync();
        return _lastTools;
    }

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var invocation = new SkillInvocation(toolName, arguments);
        var result = await invoker.InvokeAsync(invocation, cancellationToken);
        if (result.IsSuccess)
        {
            return result.Payload;
        }

        // The MCP server already maps SkillNotFoundException to a JSON-RPC
        // method-not-found; translate the catalog's SKILL_NOT_FOUND /
        // BOUNDARY_BLOCKED codes into that shape so the model sees a
        // consistent diagnostic.
        if (string.Equals(result.ErrorCode, "SKILL_NOT_FOUND", StringComparison.Ordinal))
        {
            throw new SkillNotFoundException(toolName);
        }

        throw new SpringException(
            $"Skill '{toolName}' failed: {result.ErrorCode ?? "UNKNOWN"} — {result.ErrorMessage ?? "(no message)"}");
    }

    private async Task RefreshToolsAsync()
    {
        try
        {
            var skills = await catalog.EnumerateAsync(BoundaryViewContext.External, CancellationToken.None);
            _lastTools = skills.Select(s => s.Tool).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExpertiseSkillRegistry: tool refresh failed; keeping prior snapshot.");
        }
    }
}
