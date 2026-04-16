// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Auth;

using Shouldly;

using Xunit;

/// <summary>
/// Guards the invariant that every <c>*Options</c> type in the GitHub connector
/// assembly exposes a parameterless constructor, so <c>OptionsFactory&lt;T&gt;</c>
/// can instantiate and bind it.
/// </summary>
/// <remarks>
/// See <c>Cvoya.Spring.Dapr.Tests.Configuration.OptionsInstantiationTests</c> for
/// the full rationale. Duplicated here because the test's target assembly is
/// <c>Cvoya.Spring.Connector.GitHub</c>, which the Dapr test project does not reference.
/// </remarks>
public class OptionsInstantiationTests
{
    private static readonly Assembly ScannedAssembly = typeof(GitHubConnectorOptions).Assembly;

    private static readonly HashSet<string> Exclusions = new(StringComparer.Ordinal);

    [Fact]
    public void AllOptionsTypes_HaveParameterlessConstructor()
    {
        var violations = new List<string>();

        foreach (var type in EnumerateOptionsLikeTypes(ScannedAssembly))
        {
            if (Exclusions.Contains(type.FullName!))
            {
                continue;
            }

            if (type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null) is null)
            {
                violations.Add(
                    $"{type.FullName} lacks a parameterless ctor. " +
                    "Convert positional records to non-positional records with init properties, " +
                    "or use a plain class.");
            }
        }

        violations.ShouldBeEmpty(
            "every *Options type must be new()-able so OptionsFactory<T> can bind it from IConfiguration");
    }

    private static IEnumerable<Type> EnumerateOptionsLikeTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (var t in types)
        {
            if (t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && t.Name.EndsWith("Options", StringComparison.Ordinal)
                && !typeof(Delegate).IsAssignableFrom(t))
            {
                yield return t;
            }
        }
    }
}