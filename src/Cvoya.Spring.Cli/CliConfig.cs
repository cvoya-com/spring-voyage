// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Text.Json;

/// <summary>
/// CLI configuration loaded from ~/.spring/config.json.
/// Stores the API endpoint and authentication token.
/// </summary>
public class CliConfig
{
    private static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spring");

    private static readonly string ConfigFilePath = Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// The Spring API endpoint URL.
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:5000";

    /// <summary>
    /// The API authentication token.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// Loads the CLI configuration from ~/.spring/config.json.
    /// Returns default configuration if the file does not exist.
    /// </summary>
    public static CliConfig Load()
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new CliConfig();
        }

        var json = File.ReadAllText(ConfigFilePath);
        return JsonSerializer.Deserialize<CliConfig>(json) ?? new CliConfig();
    }

    /// <summary>
    /// Saves the CLI configuration to ~/.spring/config.json.
    /// Creates the directory if it does not exist.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(ConfigFilePath, json);
    }
}
