/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Dapr.Tools;

using Cvoya.Spring.Core.Tools;

/// <summary>
/// Registry that holds all available platform tools, keyed by name.
/// Provides registration, lookup, and enumeration of tools.
/// </summary>
public class PlatformToolRegistry
{
    private readonly Dictionary<string, IPlatformTool> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a platform tool. If a tool with the same name already exists, it is replaced.
    /// </summary>
    /// <param name="tool">The tool to register.</param>
    public void Register(IPlatformTool tool) => _tools[tool.Name] = tool;

    /// <summary>
    /// Gets a tool by name, or <c>null</c> if no tool with the given name is registered.
    /// </summary>
    /// <param name="name">The tool name to look up.</param>
    /// <returns>The platform tool, or <c>null</c>.</returns>
    public IPlatformTool? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>
    /// Returns all registered platform tools.
    /// </summary>
    /// <returns>A read-only list of all registered tools.</returns>
    public IReadOnlyList<IPlatformTool> GetAll() => _tools.Values.ToList();
}
