// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Loader and DTO for the Ollama runtime's <c>agent-runtimes/ollama/seed.json</c>
/// catalog. The file ships as an embedded resource so the runtime is
/// self-contained — no host-side path lookups, no copy-to-output drift.
/// </summary>
/// <remarks>
/// <para>
/// The seed file format is the cross-runtime contract documented in
/// <c>src/Cvoya.Spring.Core/AgentRuntimes/README.md</c>. Each runtime
/// project is responsible for loading and surfacing its own seed; the
/// contract just standardises the JSON shape so consumers (tenant
/// bootstrap, install service) can read every runtime's seed uniformly.
/// </para>
/// </remarks>
public static class OllamaSeed
{
    /// <summary>
    /// The on-disk path of the seed file under the project root. Surfaced as
    /// a constant so external tooling (smoke tests, packaging scripts) can
    /// discover the canonical location without reflecting on the assembly.
    /// </summary>
    public const string ResourcePath = "agent-runtimes/ollama/seed.json";

    private const string EmbeddedResourceName =
        "Cvoya.Spring.AgentRuntimes.Ollama.agent-runtimes.ollama.seed.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Loads the seed catalog from the assembly's embedded resource and
    /// projects it into the
    /// <see cref="IAgentRuntime.DefaultModels"/> shape.
    /// </summary>
    /// <returns>The parsed seed file.</returns>
    /// <exception cref="SpringException">If the embedded resource is missing or the JSON is malformed.</exception>
    public static OllamaSeedFile Load()
    {
        var assembly = typeof(OllamaSeed).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new SpringException(
                $"Ollama seed catalog '{EmbeddedResourceName}' is missing from {assembly.GetName().Name}. " +
                "This indicates a packaging defect — verify the EmbeddedResource entry in the csproj.");

        OllamaSeedFile? seed;
        try
        {
            seed = JsonSerializer.Deserialize<OllamaSeedFile>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SpringException(
                $"Failed to parse Ollama seed catalog '{ResourcePath}': {ex.Message}", ex);
        }

        if (seed is null)
        {
            throw new SpringException(
                $"Ollama seed catalog '{ResourcePath}' deserialised to null.");
        }

        if (seed.Models is null || seed.Models.Count == 0)
        {
            throw new SpringException(
                $"Ollama seed catalog '{ResourcePath}' has an empty 'models' list. " +
                "At least one model id is required.");
        }

        if (string.IsNullOrWhiteSpace(seed.DefaultModel))
        {
            throw new SpringException(
                $"Ollama seed catalog '{ResourcePath}' is missing a 'defaultModel'.");
        }

        if (!seed.Models.Contains(seed.DefaultModel, StringComparer.Ordinal))
        {
            throw new SpringException(
                $"Ollama seed catalog '{ResourcePath}': defaultModel '{seed.DefaultModel}' is not present in 'models'.");
        }

        return seed;
    }

    /// <summary>
    /// Materialises the seed file into the <see cref="ModelDescriptor"/>
    /// shape exposed by <see cref="IAgentRuntime.DefaultModels"/>. The
    /// <c>ContextWindow</c> field is unset because Ollama tags do not carry
    /// a stable per-tag context-window declaration — surfaced as <c>null</c>
    /// so the wizard can fall back to a model-family heuristic.
    /// </summary>
    /// <param name="seed">The parsed seed file.</param>
    public static IReadOnlyList<ModelDescriptor> ToDescriptors(OllamaSeedFile seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var list = new List<ModelDescriptor>(seed.Models!.Count);
        foreach (var id in seed.Models)
        {
            list.Add(new ModelDescriptor(id, id, ContextWindow: null));
        }

        return list;
    }
}

/// <summary>
/// Strongly-typed projection of <c>agent-runtimes/ollama/seed.json</c>.
/// </summary>
/// <param name="Models">Seed list of model ids the runtime supports out of the box.</param>
/// <param name="DefaultModel">The model id selected by default when a tenant installs this runtime. Must appear in <paramref name="Models"/>.</param>
/// <param name="BaseUrl">The default base URL for the Ollama endpoint. The OSS deployment defaults to <c>http://spring-ollama:11434</c>.</param>
public sealed record OllamaSeedFile(
    [property: JsonPropertyName("models")] IReadOnlyList<string>? Models,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl);