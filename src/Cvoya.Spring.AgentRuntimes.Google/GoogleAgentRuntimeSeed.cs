// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Deserialised representation of the runtime's <c>seed.json</c> file (see
/// <c>agent-runtimes/google/seed.json</c>). The schema mirrors the contract
/// documented in
/// <c>src/Cvoya.Spring.Core/AgentRuntimes/README.md</c>; out-of-band fields
/// are tolerated so future schema additions roll out without a breaking
/// change.
/// </summary>
/// <param name="Models">Seed list of model ids the runtime supports out of the box.</param>
/// <param name="DefaultModel">The model id selected by default in the wizard. Must appear in <paramref name="Models"/>.</param>
/// <param name="BaseUrl">Optional default base URL for the runtime's API. <c>null</c> when the seed does not pin a value.</param>
public sealed record GoogleAgentRuntimeSeed(
    [property: JsonPropertyName("models")] IReadOnlyList<string> Models,
    [property: JsonPropertyName("defaultModel")] string DefaultModel,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl = null);

/// <summary>
/// Loader for <see cref="GoogleAgentRuntimeSeed"/>. The seed file is shipped
/// alongside the assembly at <c>agent-runtimes/google/seed.json</c> and
/// copied to the build output by the project's <c>None</c> item group.
/// </summary>
internal static class GoogleAgentRuntimeSeedLoader
{
    /// <summary>The relative path of the seed file from the assembly directory.</summary>
    public const string SeedFileRelativePath = "agent-runtimes/google/seed.json";

    /// <summary>
    /// Loads the seed file from the directory containing the runtime
    /// assembly. The path is relative so the same loader works in
    /// development (bin/Debug), in published containers, and in NuGet
    /// content-files layouts.
    /// </summary>
    public static GoogleAgentRuntimeSeed LoadFromAssemblyDirectory()
    {
        var assemblyLocation = typeof(GoogleAgentRuntimeSeedLoader).Assembly.Location;
        var directory = string.IsNullOrEmpty(assemblyLocation)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(assemblyLocation) ?? AppContext.BaseDirectory;

        var seedPath = Path.Combine(directory, SeedFileRelativePath);
        return LoadFromFile(seedPath);
    }

    /// <summary>
    /// Loads and validates the seed file at <paramref name="seedFilePath"/>.
    /// Throws <see cref="FileNotFoundException"/> when missing and
    /// <see cref="InvalidDataException"/> when the file is malformed or the
    /// declared <see cref="GoogleAgentRuntimeSeed.DefaultModel"/> does not
    /// appear in <see cref="GoogleAgentRuntimeSeed.Models"/>.
    /// </summary>
    public static GoogleAgentRuntimeSeed LoadFromFile(string seedFilePath)
    {
        if (!File.Exists(seedFilePath))
        {
            throw new FileNotFoundException(
                $"Google agent runtime seed file not found at '{seedFilePath}'. " +
                $"The file is expected to ship alongside the assembly at the relative path '{SeedFileRelativePath}'.",
                seedFilePath);
        }

        using var stream = File.OpenRead(seedFilePath);
        return LoadFromStream(stream, seedFilePath);
    }

    /// <summary>
    /// Loads and validates a seed payload from <paramref name="utf8Stream"/>.
    /// <paramref name="sourceDescription"/> is used purely for error messages.
    /// </summary>
    public static GoogleAgentRuntimeSeed LoadFromStream(Stream utf8Stream, string sourceDescription)
    {
        GoogleAgentRuntimeSeed? seed;
        try
        {
            seed = JsonSerializer.Deserialize(utf8Stream, GoogleAgentRuntimeSeedJsonContext.Default.GoogleAgentRuntimeSeed);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Google agent runtime seed file at '{sourceDescription}' is not valid JSON: {ex.Message}",
                ex);
        }

        if (seed is null)
        {
            throw new InvalidDataException(
                $"Google agent runtime seed file at '{sourceDescription}' deserialised to null.");
        }

        if (seed.Models is null || seed.Models.Count == 0)
        {
            throw new InvalidDataException(
                $"Google agent runtime seed file at '{sourceDescription}' must declare at least one model in 'models'.");
        }

        if (string.IsNullOrWhiteSpace(seed.DefaultModel))
        {
            throw new InvalidDataException(
                $"Google agent runtime seed file at '{sourceDescription}' must declare a non-empty 'defaultModel'.");
        }

        if (!seed.Models.Contains(seed.DefaultModel, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Google agent runtime seed file at '{sourceDescription}' declares defaultModel='{seed.DefaultModel}' which is not in the 'models' list.");
        }

        return seed;
    }
}

[JsonSerializable(typeof(GoogleAgentRuntimeSeed))]
internal partial class GoogleAgentRuntimeSeedJsonContext : JsonSerializerContext;