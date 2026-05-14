// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Reflection;

using Octokit;

/// <summary>
/// Reflection-based factory for Octokit <see cref="RepositoryHook"/> instances.
/// Octokit keeps the hook constructor arity internal, so production code never
/// calls it — but the skill tests need to hand a configured hook to a mocked
/// <see cref="IGitHubClient"/>. Centralised here so the trick lives in one
/// place.
/// </summary>
internal static class WebhookTestHelpers
{
    public static RepositoryHook CreateRepositoryHook(
        int id,
        string name = "web",
        bool active = true,
        IReadOnlyList<string>? events = null,
        IReadOnlyDictionary<string, string>? config = null)
    {
        var ctor = typeof(RepositoryHook)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p =>
        {
            if (p.Name == "id") return (object?)id;
            if (p.Name == "name") return name;
            if (p.Name == "active") return active;
            if (p.Name == "events") return events ?? (IReadOnlyList<string>)Array.Empty<string>();
            if (p.Name == "config") return config
                ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.Ordinal);
            if (p.ParameterType == typeof(string)) return string.Empty;
            if (p.ParameterType == typeof(long)) return 0L;
            if (p.ParameterType == typeof(int)) return 0;
            if (p.ParameterType == typeof(bool)) return false;
            if (p.ParameterType == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            return null;
        }).ToArray();

        return (RepositoryHook)ctor.Invoke(args);
    }
}
