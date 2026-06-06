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
    /// <summary>
    /// Default config path (<c>~/.spring/config.json</c>). Computed per access so
    /// a changed home directory — e.g. <c>$HOME</c> redirected in a test — is
    /// honoured rather than frozen at type-init time.
    /// </summary>
    internal static string DefaultConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".spring",
        "config.json");

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
    public static CliConfig Load() => Load(DefaultConfigFilePath);

    /// <summary>
    /// Loads the CLI configuration from <paramref name="configFilePath"/>.
    /// Test seam for the default-path <see cref="Load()"/>.
    /// </summary>
    internal static CliConfig Load(string configFilePath)
    {
        if (!File.Exists(configFilePath))
        {
            return new CliConfig();
        }

        var json = File.ReadAllText(configFilePath);
        return JsonSerializer.Deserialize<CliConfig>(json) ?? new CliConfig();
    }

    /// <summary>
    /// Saves the CLI configuration to ~/.spring/config.json.
    /// Creates the directory if it does not exist.
    /// </summary>
    public void Save() => Save(DefaultConfigFilePath);

    /// <summary>
    /// Saves the CLI configuration to <paramref name="configFilePath"/>.
    /// Test seam for the default-path <see cref="Save()"/>.
    /// </summary>
    internal void Save(string configFilePath)
    {
        var directory = Path.GetDirectoryName(configFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(configFilePath, json);

        // config.json may hold a bearer token (ApiToken), so lock it down to the
        // owner. This is the single chokepoint for every writer — the installer
        // used to chmod the file itself before it delegated the write here. No
        // POSIX mode bits on Windows, so this is a no-op there.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                configFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
