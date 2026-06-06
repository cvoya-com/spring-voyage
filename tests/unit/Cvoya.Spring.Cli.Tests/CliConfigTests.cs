// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using Shouldly;

using Xunit;

public class CliConfigTests
{
    [Fact]
    public void Load_NoConfigFile_ReturnsDefaultEndpoint()
    {
        var config = new CliConfig();

        config.Endpoint.ShouldBe("http://localhost:5000");
    }

    [Fact]
    public void Load_NoConfigFile_ReturnsNullApiToken()
    {
        var config = new CliConfig();

        config.ApiToken.ShouldBeNull();
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var (dir, path) = TempConfigPath();
        try
        {
            // File is never created — Load should fall back to defaults, not throw.
            var loaded = CliConfig.Load(path);

            loaded.Endpoint.ShouldBe("http://localhost:5000");
            loaded.ApiToken.ShouldBeNull();
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var (dir, path) = TempConfigPath();
        try
        {
            new CliConfig
            {
                Endpoint = "https://api.example.com",
                ApiToken = "test-token-123",
            }.Save(path);

            var loaded = CliConfig.Load(path);

            loaded.Endpoint.ShouldBe("https://api.example.com");
            loaded.ApiToken.ShouldBe("test-token-123");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // #3091: re-pointing the CLI at a changed Caddy host port must refresh the
    // endpoint while preserving an existing auth token. This is exactly the
    // load → mutate → save merge that `spring config set endpoint` performs, so
    // the absent-only bash write the installer used to do is no longer needed.
    [Fact]
    public void SetEndpointMerge_UpdatesEndpoint_PreservesToken()
    {
        var (dir, path) = TempConfigPath();
        try
        {
            // Pre-existing config with a token, as if a prior install + token
            // creation had populated it.
            new CliConfig
            {
                Endpoint = "http://localhost:8081",
                ApiToken = "preserve-me",
            }.Save(path);

            // The merge `spring config set endpoint http://localhost:8082` runs.
            var config = CliConfig.Load(path);
            config.Endpoint = "http://localhost:8082";
            config.Save(path);

            var reloaded = CliConfig.Load(path);
            reloaded.Endpoint.ShouldBe("http://localhost:8082");
            reloaded.ApiToken.ShouldBe("preserve-me");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Save_LocksDownFileToOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // No POSIX mode bits on Windows.
        }

        var (dir, path) = TempConfigPath();
        try
        {
            new CliConfig
            {
                Endpoint = "http://localhost",
                ApiToken = "secret",
            }.Save(path);

            File.GetUnixFileMode(path)
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static (string dir, string path) TempConfigPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        return (dir, Path.Combine(dir, "config.json"));
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
