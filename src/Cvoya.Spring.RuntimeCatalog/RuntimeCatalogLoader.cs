// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.RuntimeCatalog;

using System.IO;
using System.Reflection;

using Cvoya.Spring.Core.Catalog;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Deserialises the platform runtime catalogue (ADR-0038) from
/// <c>platform/runtime-catalog.yaml</c>. Reuses the YamlDotNet pattern
/// established in <c>Cvoya.Spring.Manifest.ManifestParser</c>: camel-case
/// naming, ignore-unmatched, deserialize-and-throw on malformed input.
/// </summary>
/// <remarks>
/// <para>
/// The loader produces an immutable <see cref="IRuntimeCatalog"/>; callers
/// hold one instance for the process lifetime. Hot-reload is out of scope
/// for v0.1.
/// </para>
/// <para>
/// CI lints the YAML against <c>runtime-catalog.schema.json</c> separately
/// (no JSON Schema runtime dependency in this project). The loader's own
/// validation is light: it confirms the deserialised payload is non-null
/// and that every per-edge auth method on a runtime is declared on the
/// referenced provider — the structural part of the schema check.
/// </para>
/// </remarks>
public static class RuntimeCatalogLoader
{
    /// <summary>
    /// Resource name of the embedded catalogue file. Matches the
    /// <c>LogicalName</c> in <c>Cvoya.Spring.RuntimeCatalog.csproj</c>.
    /// </summary>
    public const string EmbeddedResourceName = "Cvoya.Spring.RuntimeCatalog.runtime-catalog.yaml";

    /// <summary>
    /// Loads the catalogue from the embedded resource shipped with this
    /// assembly. The default boot path for hosts that have not configured
    /// an alternate location.
    /// </summary>
    public static IRuntimeCatalog LoadEmbedded()
    {
        var assembly = typeof(RuntimeCatalogLoader).Assembly;
        return LoadEmbedded(assembly);
    }

    /// <summary>
    /// Test seam — loads from an arbitrary assembly's embedded resource.
    /// </summary>
    internal static IRuntimeCatalog LoadEmbedded(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedResourceName}' was not found. " +
                "Verify the EmbeddedResource entry in Cvoya.Spring.RuntimeCatalog.csproj.");
        using var reader = new StreamReader(stream);
        var yaml = reader.ReadToEnd();
        return LoadFromYaml(yaml);
    }

    /// <summary>
    /// Loads the catalogue from an on-disk YAML file (e.g. for tenant
    /// overrides — see v0.2 follow-up issue).
    /// </summary>
    public static IRuntimeCatalog LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var yaml = File.ReadAllText(path);
        return LoadFromYaml(yaml);
    }

    /// <summary>
    /// Loads the catalogue from a YAML string. Throws
    /// <see cref="InvalidOperationException"/> on malformed input or when
    /// the structural cross-checks fail.
    /// </summary>
    public static IRuntimeCatalog LoadFromYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        RuntimeCatalogYaml? doc;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            doc = deserializer.Deserialize<RuntimeCatalogYaml>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidOperationException(
                $"runtime-catalog.yaml is not valid YAML: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new InvalidOperationException("runtime-catalog.yaml is empty.");
        }

        var providers = (doc.ModelProviders ?? new List<ModelProviderYaml>())
            .Select(MapProvider)
            .ToList();

        var providerIds = providers.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runtimes = (doc.AgentRuntimes ?? new List<AgentRuntimeYaml>())
            .Select(r => MapRuntime(r, providers))
            .ToList();

        // Structural cross-check: every per-edge provider id references a declared provider.
        foreach (var runtime in runtimes)
        {
            foreach (var edge in runtime.ModelProviders)
            {
                if (!providerIds.Contains(edge.Id))
                {
                    throw new InvalidOperationException(
                        $"runtime-catalog.yaml: agentRuntime '{runtime.Id}' references unknown provider '{edge.Id}'.");
                }
            }
        }

        return new ImmutableRuntimeCatalog(runtimes, providers);
    }

    private static ModelProvider MapProvider(ModelProviderYaml y)
    {
        ArgumentNullException.ThrowIfNull(y);

        var contract = y.LlmApiContract
            ?? throw new InvalidOperationException(
                $"runtime-catalog.yaml: modelProvider '{y.Id}' is missing 'llmApiContract'.");

        return new ModelProvider(
            Id: Require(y.Id, $"modelProvider missing 'id'"),
            DisplayName: Require(y.DisplayName, $"modelProvider '{y.Id}' missing 'displayName'"),
            ApiBaseUrl: Require(y.ApiBaseUrl, $"modelProvider '{y.Id}' missing 'apiBaseUrl'"),
            ModelsEndpoint: Require(y.ModelsEndpoint, $"modelProvider '{y.Id}' missing 'modelsEndpoint'"),
            Adapter: Require(y.Adapter, $"modelProvider '{y.Id}' missing 'adapter'"),
            AuthMethods: (y.AuthMethods ?? new List<string>()).Select(ParseAuthMethod).ToArray(),
            LlmApiContract: new LlmApiContract(
                Name: Require(contract.Name, $"modelProvider '{y.Id}' missing 'llmApiContract.name'"),
                Version: Require(contract.Version, $"modelProvider '{y.Id}' missing 'llmApiContract.version'")),
            DefaultModels: (y.DefaultModels ?? new List<string>()).ToArray());
    }

    private static Cvoya.Spring.Core.Catalog.AgentRuntime MapRuntime(
        AgentRuntimeYaml y,
        IReadOnlyList<ModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(y);

        var threadBinding = y.ThreadBinding
            ?? throw new InvalidOperationException(
                $"runtime-catalog.yaml: agentRuntime '{y.Id}' is missing 'threadBinding'.");
        var promptInjection = y.SystemPromptInjection
            ?? throw new InvalidOperationException(
                $"runtime-catalog.yaml: agentRuntime '{y.Id}' is missing 'systemPromptInjection'.");

        return new Cvoya.Spring.Core.Catalog.AgentRuntime(
            Id: Require(y.Id, "agentRuntime missing 'id'"),
            DisplayName: Require(y.DisplayName, $"agentRuntime '{y.Id}' missing 'displayName'"),
            DefaultImage: Require(y.DefaultImage, $"agentRuntime '{y.Id}' missing 'defaultImage'"),
            Launcher: Require(y.Launcher, $"agentRuntime '{y.Id}' missing 'launcher'"),
            ThreadBinding: new ThreadBinding(
                Kind: ParseThreadBindingKind(threadBinding.Kind, y.Id!),
                ArgName: threadBinding.ArgName,
                EnvVarName: threadBinding.EnvVarName),
            SystemPromptInjection: new SystemPromptInjection(
                Kind: ParseSystemPromptInjectionKind(promptInjection.Kind, y.Id!),
                FilePath: promptInjection.FilePath,
                EnvVarName: promptInjection.EnvVarName,
                ArgName: promptInjection.ArgName),
            ModelProviders: (y.ModelProviders ?? new List<AgentRuntimeProviderEdgeYaml>())
                .Select(e => MapEdge(e, y.Id!, providers))
                .ToArray());
    }

    private static AgentRuntimeProviderEdge MapEdge(
        AgentRuntimeProviderEdgeYaml y,
        string runtimeId,
        IReadOnlyList<ModelProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(y);

        var providerId = Require(y.Id, $"agentRuntime '{runtimeId}' has an edge with no 'id'");
        AuthMethod? authMethod = string.IsNullOrWhiteSpace(y.AuthMethod)
            ? null
            : ParseAuthMethod(y.AuthMethod!);

        // Cross-check: per-edge authMethod must be declared on the referenced
        // provider's authMethods list (the structural piece of decision 4).
        if (authMethod is not null)
        {
            var provider = providers.FirstOrDefault(p =>
                string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (provider is not null && !provider.AuthMethods.Contains(authMethod.Value))
            {
                throw new InvalidOperationException(
                    $"runtime-catalog.yaml: agentRuntime '{runtimeId}' edge '{providerId}' " +
                    $"declares authMethod '{y.AuthMethod}' which is not in the provider's authMethods list.");
            }
        }

        return new AgentRuntimeProviderEdge(
            Id: providerId,
            AuthMethod: authMethod,
            CredentialEnvVar: y.CredentialEnvVar);
    }

    private static AuthMethod ParseAuthMethod(string value) => value switch
    {
        "oauth" => AuthMethod.Oauth,
        "api-key" => AuthMethod.ApiKey,
        _ => throw new InvalidOperationException(
            $"runtime-catalog.yaml: unsupported authMethod '{value}' (expected 'oauth' or 'api-key')."),
    };

    private static ThreadBindingKind ParseThreadBindingKind(string? value, string runtimeId) => value switch
    {
        "cli-arg" => ThreadBindingKind.CliArg,
        "env-var" => ThreadBindingKind.EnvVar,
        "none" => ThreadBindingKind.None,
        _ => throw new InvalidOperationException(
            $"runtime-catalog.yaml: agentRuntime '{runtimeId}' has unsupported threadBinding.kind '{value}'."),
    };

    private static SystemPromptInjectionKind ParseSystemPromptInjectionKind(string? value, string runtimeId) => value switch
    {
        "file" => SystemPromptInjectionKind.File,
        "env-var" => SystemPromptInjectionKind.EnvVar,
        "argv" => SystemPromptInjectionKind.Argv,
        _ => throw new InvalidOperationException(
            $"runtime-catalog.yaml: agentRuntime '{runtimeId}' has unsupported systemPromptInjection.kind '{value}'."),
    };

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"runtime-catalog.yaml: {message}.")
            : value!;
}

/// <summary>Default in-memory <see cref="IRuntimeCatalog"/> implementation.</summary>
internal sealed class ImmutableRuntimeCatalog : IRuntimeCatalog
{
    private readonly Dictionary<string, Cvoya.Spring.Core.Catalog.AgentRuntime> _runtimesById;
    private readonly Dictionary<string, ModelProvider> _providersById;

    public ImmutableRuntimeCatalog(
        IReadOnlyList<Cvoya.Spring.Core.Catalog.AgentRuntime> runtimes,
        IReadOnlyList<ModelProvider> providers)
    {
        AgentRuntimes = runtimes;
        ModelProviders = providers;
        _runtimesById = runtimes.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        _providersById = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<Cvoya.Spring.Core.Catalog.AgentRuntime> AgentRuntimes { get; }

    public IReadOnlyList<ModelProvider> ModelProviders { get; }

    public Cvoya.Spring.Core.Catalog.AgentRuntime? GetAgentRuntime(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return _runtimesById.GetValueOrDefault(id);
    }

    public ModelProvider? GetModelProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return _providersById.GetValueOrDefault(id);
    }
}

// Internal YAML-shape DTOs. Camel-case names match the catalogue file via
// CamelCaseNamingConvention; nullable types let the loader produce
// precise errors instead of opaque YamlDotNet stack traces.

internal sealed class RuntimeCatalogYaml
{
    public List<ModelProviderYaml>? ModelProviders { get; set; }
    public List<AgentRuntimeYaml>? AgentRuntimes { get; set; }
}

internal sealed class ModelProviderYaml
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? ApiBaseUrl { get; set; }
    public string? ModelsEndpoint { get; set; }
    public string? Adapter { get; set; }
    public List<string>? AuthMethods { get; set; }
    public LlmApiContractYaml? LlmApiContract { get; set; }
    public List<string>? DefaultModels { get; set; }
}

internal sealed class LlmApiContractYaml
{
    public string? Name { get; set; }
    public string? Version { get; set; }
}

internal sealed class AgentRuntimeYaml
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? DefaultImage { get; set; }
    public string? Launcher { get; set; }
    public ThreadBindingYaml? ThreadBinding { get; set; }
    public SystemPromptInjectionYaml? SystemPromptInjection { get; set; }
    public List<AgentRuntimeProviderEdgeYaml>? ModelProviders { get; set; }
}

internal sealed class ThreadBindingYaml
{
    public string? Kind { get; set; }
    public string? ArgName { get; set; }
    public string? EnvVarName { get; set; }
}

internal sealed class SystemPromptInjectionYaml
{
    public string? Kind { get; set; }
    public string? FilePath { get; set; }
    public string? EnvVarName { get; set; }
    public string? ArgName { get; set; }
}

internal sealed class AgentRuntimeProviderEdgeYaml
{
    public string? Id { get; set; }
    public string? AuthMethod { get; set; }
    public string? CredentialEnvVar { get; set; }
}
