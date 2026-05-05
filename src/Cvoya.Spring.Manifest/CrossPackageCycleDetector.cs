// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;

/// <summary>
/// Detects reference cycles in the post-resolve artefact graph, including
/// edges that cross package boundaries (ADR-0037 decision 4).
/// <see cref="LocalSymbolMap"/> covers within-package cycles; this
/// detector covers cycles that traverse <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
/// references resolved through the catalog.
/// </summary>
/// <remarks>
/// <para>
/// Each node in the graph is a
/// <see cref="ArtefactNode"/> tuple of <c>(package, kind, name, version)</c>.
/// Two installed versions of the same artefact are different nodes — this
/// is intentional per ADR-0037 decision 5 ("a reference to <c>pkg-b/agent-x@1.0.0</c>
/// and a reference to <c>pkg-b/agent-x@2.0.0</c> are different nodes and
/// do not collide on the cycle check"). If two installed versions of the
/// same artefact transitively reference each other, that's a cycle and
/// is rejected.
/// </para>
/// <para>
/// The detector is purely a graph walk over edges supplied by the caller.
/// It does not own the resolution path; the caller (today
/// <see cref="PackageManifestParser"/>) computes the edges from
/// <c>content:</c> entries, <c>members:</c> entries on resolved units,
/// and any other manifest field that holds a kind-discriminated
/// reference.
/// </para>
/// </remarks>
public sealed class CrossPackageCycleDetector
{
    private readonly Dictionary<ArtefactNode, IReadOnlyList<ArtefactNode>> _outEdges =
        new();

    /// <summary>
    /// Register a node and its outgoing edges. Subsequent calls for the
    /// same node overwrite earlier ones.
    /// </summary>
    public void AddNode(ArtefactNode node, IReadOnlyList<ArtefactNode> outEdges)
    {
        _outEdges[node] = outEdges;
    }

    /// <summary>
    /// Walk the graph and return the first cycle found, or <c>null</c> if
    /// the graph is acyclic. The returned path starts and ends with the
    /// same node.
    /// </summary>
    public IReadOnlyList<ArtefactNode>? FindCycle()
    {
        var visited = new HashSet<ArtefactNode>();
        var onStack = new HashSet<ArtefactNode>();
        var stack = new List<ArtefactNode>();

        foreach (var node in _outEdges.Keys)
        {
            if (visited.Contains(node))
            {
                continue;
            }

            var cycle = Visit(node, visited, onStack, stack);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    private IReadOnlyList<ArtefactNode>? Visit(
        ArtefactNode node,
        HashSet<ArtefactNode> visited,
        HashSet<ArtefactNode> onStack,
        List<ArtefactNode> stack)
    {
        visited.Add(node);
        onStack.Add(node);
        stack.Add(node);

        if (_outEdges.TryGetValue(node, out var edges))
        {
            foreach (var next in edges)
            {
                if (onStack.Contains(next))
                {
                    var cycleStart = stack.IndexOf(next);
                    var cycle = new List<ArtefactNode>(stack.Count - cycleStart + 1);
                    for (var i = cycleStart; i < stack.Count; i++)
                    {
                        cycle.Add(stack[i]);
                    }
                    cycle.Add(next);
                    return cycle;
                }

                if (!visited.Contains(next))
                {
                    var cycle = Visit(next, visited, onStack, stack);
                    if (cycle is not null)
                    {
                        return cycle;
                    }
                }
            }
        }

        onStack.Remove(node);
        stack.RemoveAt(stack.Count - 1);
        return null;
    }
}

/// <summary>
/// One node in the cross-package artefact graph. Two installed versions
/// of the same artefact are distinct nodes (ADR-0037 decision 5).
/// </summary>
/// <param name="Package">
/// The source package name. <c>null</c> denotes the package currently
/// being resolved (within-package nodes).
/// </param>
/// <param name="Kind">The artefact kind.</param>
/// <param name="Name">The artefact's local name.</param>
/// <param name="Version">
/// The package version this artefact came from. <c>null</c> for
/// within-package nodes (which inherit the resolving package's version).
/// </param>
public readonly record struct ArtefactNode(
    string? Package,
    ArtefactKind Kind,
    string Name,
    string? Version);