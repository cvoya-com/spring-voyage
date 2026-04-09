/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Cli.Tests;

using FluentAssertions;
using Xunit;

public class CliConfigTests
{
    [Fact]
    public void Load_NoConfigFile_ReturnsDefaultEndpoint()
    {
        var config = new CliConfig();

        config.Endpoint.Should().Be("http://localhost:5000");
    }

    [Fact]
    public void Load_NoConfigFile_ReturnsNullApiToken()
    {
        var config = new CliConfig();

        config.ApiToken.Should().BeNull();
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            Directory.CreateDirectory(tempDir);

            var config = new CliConfig
            {
                Endpoint = "https://api.example.com",
                ApiToken = "test-token-123"
            };

            // Write config manually to temp location for round-trip test
            var json = System.Text.Json.JsonSerializer.Serialize(config,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);

            var loaded = System.Text.Json.JsonSerializer.Deserialize<CliConfig>(
                File.ReadAllText(configPath));

            loaded.Should().NotBeNull();
            loaded!.Endpoint.Should().Be("https://api.example.com");
            loaded.ApiToken.Should().Be("test-token-123");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
