// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Guards the invariant that every <c>*Options</c> type in the Spring platform
/// assemblies exposes a parameterless constructor.
/// </summary>
/// <remarks>
/// <para>
/// <c>OptionsFactory&lt;T&gt;</c> (used by <c>IOptions&lt;T&gt;</c> binding) calls
/// <c>new T()</c> before populating properties from configuration. Positional
/// records compile to a type that has ONLY the positional constructor, so they
/// silently break every <c>IOptions&lt;T&gt;</c> consumer — actor activation
/// throws <c>MissingMethodException</c> deep inside Dapr's runtime.
/// </para>
/// <para>
/// This is the same failure mode that blocked issue #338 (Tier1Options) end-to-end
/// in production after build succeeded. The test exists so the compiler can catch
/// the regression at build time instead.
/// </para>
/// </remarks>
public class OptionsInstantiationTests
{
    /// <summary>Assemblies in which any <c>*Options</c> type must be instantiable via <c>new()</c>.</summary>
    private static readonly Assembly[] ScannedAssemblies =
    [
        typeof(AnthropicProvider).Assembly,          // Cvoya.Spring.Dapr
    ];

    /// <summary>
    /// Types that look like options by name but are intentionally NOT wired via
    /// <c>IOptions&lt;T&gt;</c>. These are allowed to lack a parameterless ctor.
    /// Keep this list extremely short — each entry is a liability.
    /// </summary>
    private static readonly HashSet<string> Exclusions = new(StringComparer.Ordinal);

    [Fact]
    public void AllOptionsTypes_HaveParameterlessConstructor()
    {
        var violations = new List<string>();

        foreach (var asm in ScannedAssemblies)
        {
            foreach (var type in EnumerateOptionsLikeTypes(asm))
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
                        $"{type.FullName} (in {asm.GetName().Name}) lacks a parameterless ctor. " +
                        "Convert positional records to non-positional records with init properties, " +
                        "or use a plain class.");
                }
            }
        }

        violations.ShouldBeEmpty(
            "every *Options type must be new()-able so OptionsFactory<T> can bind it from IConfiguration");
    }

    [Fact]
    public void Tier1Options_BindsFromConfiguration_AndIsResolvableFromDI()
    {
        // Regression test for issue #338: positional-record Tier1Options caused
        // AgentActor activation to fail with "No parameterless constructor defined".
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Initiative:Tier1:OllamaBaseUrl"] = "http://ollama.example:11434",
                ["Initiative:Tier1:Model"] = "phi-3-mini-custom",
                ["Initiative:Tier1:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<Tier1Options>().BindConfiguration("Initiative:Tier1");

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<Tier1Options>>().Value;

        options.ShouldNotBeNull();
        options.OllamaBaseUrl.ShouldBe("http://ollama.example:11434");
        options.Model.ShouldBe("phi-3-mini-custom");
        options.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Tier1Options_DefaultsApply_WhenConfigurationEmpty()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<Tier1Options>().BindConfiguration("Initiative:Tier1");

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<Tier1Options>>().Value;

        options.OllamaBaseUrl.ShouldBe("http://localhost:11434");
        options.Model.ShouldBe("phi-3-mini");
        options.Enabled.ShouldBeTrue();
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