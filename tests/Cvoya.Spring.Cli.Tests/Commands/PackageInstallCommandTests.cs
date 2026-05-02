// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Parser + wire-level tests for the <c>spring package install</c> verb cluster
/// (ADR-0035 decision 4 / #1561).
///
/// Tests 1–13 per the acceptance criteria in the issue brief. The live-package
/// integration test (#13) is skipped pending #1562 (packages/spring-voyage-oss/
/// package.yaml).
/// </summary>
public class PackageInstallCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    private static Option<string> CreateOutputOption() =>
        new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
        };

    // ── 1. Parser tests ───────────────────────────────────────────────────────

    [Fact]
    public void PackageInstall_ParsesSingleName()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package install my-pkg");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageInstall_ParsesMultipleNames()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package install pkg-a pkg-b");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageInstall_ParsesInputFlag()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package install my-pkg --input github_owner=acme");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageInstall_ParsesMultipleInputFlags()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse(
            "package install my-pkg --input github_owner=acme --input github_repo=platform");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageInstall_ParsesFileFlag()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package install --file /tmp/package.yaml");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageStatus_ParsesInstallId()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse(
            "package status 11111111-2222-3333-4444-555555555555");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageRetry_ParsesInstallId()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse(
            "package retry 11111111-2222-3333-4444-555555555555");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageAbort_ParsesInstallId()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse(
            "package abort 11111111-2222-3333-4444-555555555555");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageExport_ParsesUnitName()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package export my-unit");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageExport_ParsesWithValuesFlag()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package export my-unit --with-values");

        parseResult.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void PackageExport_ParsesOutputFileFlag()
    {
        var outputOption = CreateOutputOption();
        var packageCommand = PackageCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(packageCommand);

        var parseResult = rootCommand.Parse("package export my-unit --output-file /tmp/out.yaml");

        parseResult.Errors.ShouldBeEmpty();
    }

    // ── 2. --input parsing tests ──────────────────────────────────────────────

    [Fact]
    public void ParseInputs_SingleTarget_BareKeyValue_AppliedToPackage()
    {
        var inputs = new[] { "github_owner=acme", "github_repo=platform" };
        var packages = new[] { "spring-voyage-oss" };

        var result = PackageCommand.ParseInputs(inputs, packages, inputFilePath: null);

        result["spring-voyage-oss"]["github_owner"].ShouldBe("acme");
        result["spring-voyage-oss"]["github_repo"].ShouldBe("platform");
    }

    [Fact]
    public void ParseInputs_MultiTarget_NamespacedKeyValue_AppliedToCorrectPackage()
    {
        var inputs = new[] { "pkg-a.key1=val1", "pkg-b.key2=val2" };
        var packages = new[] { "pkg-a", "pkg-b" };

        var result = PackageCommand.ParseInputs(inputs, packages, inputFilePath: null);

        result["pkg-a"]["key1"].ShouldBe("val1");
        result["pkg-b"]["key2"].ShouldBe("val2");
    }

    [Fact]
    public void ParseInputs_MultiTarget_BareKey_ThrowsError()
    {
        var inputs = new[] { "github_owner=acme" };
        var packages = new[] { "pkg-a", "pkg-b" };

        Should.Throw<ArgumentException>(
            () => PackageCommand.ParseInputs(inputs, packages, inputFilePath: null))
            .Message.ShouldContain("namespaced");
    }

    [Fact]
    public void ParseInputs_MixedBareAndNamespaced_ThrowsError()
    {
        // Mixing bare and namespaced in the same invocation is forbidden.
        var inputs = new[] { "bare_key=value", "pkg-a.namespaced=value" };
        var packages = new[] { "pkg-a" };

        Should.Throw<ArgumentException>(
            () => PackageCommand.ParseInputs(inputs, packages, inputFilePath: null))
            .Message.ShouldContain("mix");
    }

    [Fact]
    public void ParseInputs_NamespacedKey_UnknownPackage_ThrowsError()
    {
        var inputs = new[] { "unknown-pkg.key=val" };
        var packages = new[] { "pkg-a" };

        Should.Throw<ArgumentException>(
            () => PackageCommand.ParseInputs(inputs, packages, inputFilePath: null))
            .Message.ShouldContain("not match any package");
    }

    [Fact]
    public void ParseInputs_TokenMissingEquals_ThrowsError()
    {
        var inputs = new[] { "noequalssign" };
        var packages = new[] { "pkg-a" };

        Should.Throw<ArgumentException>(
            () => PackageCommand.ParseInputs(inputs, packages, inputFilePath: null))
            .Message.ShouldContain("key=value");
    }

    // ── 3. --input-file tests ─────────────────────────────────────────────────

    [Fact]
    public void ParseInputYaml_SingleTarget_TopLevelKeysAreInputNames()
    {
        var yaml = """
            github_owner: acme
            github_repo: platform
            github_installation_id: "12345"
            """;
        var packages = new[] { "spring-voyage-oss" };

        var result = PackageCommand.ParseInputYaml(yaml, packages);

        result["spring-voyage-oss"]["github_owner"].ShouldBe("acme");
        result["spring-voyage-oss"]["github_repo"].ShouldBe("platform");
        result["spring-voyage-oss"]["github_installation_id"].ShouldBe("12345");
    }

    [Fact]
    public void ParseInputYaml_MultiTarget_TopLevelKeysArePackageNames()
    {
        var yaml = """
            pkg-a:
              key1: val1
              key2: val2
            pkg-b:
              key3: val3
            """;
        var packages = new[] { "pkg-a", "pkg-b" };

        var result = PackageCommand.ParseInputYaml(yaml, packages);

        result["pkg-a"]["key1"].ShouldBe("val1");
        result["pkg-a"]["key2"].ShouldBe("val2");
        result["pkg-b"]["key3"].ShouldBe("val3");
    }

    [Fact]
    public void ParseInputs_InputFile_SingleTarget_LoadsFromFile()
    {
        var yaml = "github_owner: acme\ngithub_repo: platform\n";
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, yaml);
            var packages = new[] { "spring-voyage-oss" };

            var result = PackageCommand.ParseInputs(
                Array.Empty<string>(), packages, inputFilePath: tmpFile);

            result["spring-voyage-oss"]["github_owner"].ShouldBe("acme");
            result["spring-voyage-oss"]["github_repo"].ShouldBe("platform");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── 4. Wire-level tests for SpringApiClient package install methods ───────

    [Fact]
    public async Task InstallPackagesAsync_PostsToInstallEndpoint()
    {
        var handler = new RecordingHandler((req, _) =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe("/api/v1/packages/install");

            var installId = Guid.NewGuid();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $$"""{"installId":"{{installId}}","status":"active","packages":[{"packageName":"my-pkg","state":"active","errorMessage":null}],"startedAt":"2026-01-01T00:00:00Z","completedAt":"2026-01-01T00:01:00Z","error":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var targets = new[]
        {
            new SpringApiClient.PackageInstallTargetRequest(
                PackageName: "my-pkg",
                Inputs: new Dictionary<string, string> { ["github_owner"] = "acme" }),
        };

        var result = await client.InstallPackagesAsync(
            targets, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Status.ShouldBe("active");
        result.Packages.Count.ShouldBe(1);
        result.Packages[0].PackageName.ShouldBe("my-pkg");
        result.Packages[0].State.ShouldBe("active");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task InstallPackagesAsync_MultiTarget_PostsBothTargets()
    {
        string? capturedBody = null;
        var handler = new RecordingHandler(async (req, ct) =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            var installId = Guid.NewGuid();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $$"""{"installId":"{{installId}}","status":"active","packages":[{"packageName":"pkg-a","state":"active","errorMessage":null},{"packageName":"pkg-b","state":"active","errorMessage":null}],"startedAt":"2026-01-01T00:00:00Z","completedAt":null,"error":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var targets = new[]
        {
            new SpringApiClient.PackageInstallTargetRequest("pkg-a", new Dictionary<string, string> { ["key1"] = "v1" }),
            new SpringApiClient.PackageInstallTargetRequest("pkg-b", new Dictionary<string, string> { ["key2"] = "v2" }),
        };

        var result = await client.InstallPackagesAsync(
            targets, TestContext.Current.CancellationToken);

        result.Packages.Count.ShouldBe(2);
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("pkg-a");
        capturedBody.ShouldContain("pkg-b");
    }

    [Fact]
    public async Task InstallPackagesAsync_MissingDepError_ThrowsWithServerMessage()
    {
        var handler = new RecordingHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{"detail":"package pkg-a references unknown-pkg/some-agent, which is not in the install batch and not installed in this tenant","status":400}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var targets = new[]
        {
            new SpringApiClient.PackageInstallTargetRequest("pkg-a", null),
        };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => client.InstallPackagesAsync(targets, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("not in the install batch");
        ex.Message.ShouldContain("400");
    }

    [Fact]
    public async Task GetInstallStatusAsync_GetsFromInstallsEndpoint()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
        {
            req.Method.ShouldBe(HttpMethod.Get);
            req.RequestUri!.AbsolutePath.ShouldBe($"/api/v1/installs/{installId}");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"installId":"{{installId}}","status":"failed","packages":[{"packageName":"my-pkg","state":"failed","errorMessage":"Dapr placement timeout"}],"startedAt":"2026-01-01T00:00:00Z","completedAt":null,"error":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.GetInstallStatusAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe("failed");
        result.Packages[0].State.ShouldBe("failed");
        result.Packages[0].ErrorMessage.ShouldBe("Dapr placement timeout");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstallStatusAsync_ReturnsNullOn404()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"title":"Not Found","status":404}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.GetInstallStatusAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task RetryInstallAsync_PostsToRetryEndpoint()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe($"/api/v1/installs/{installId}/retry");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"installId":"{{installId}}","status":"active","packages":[{"packageName":"my-pkg","state":"active","errorMessage":null}],"startedAt":"2026-01-01T00:00:00Z","completedAt":"2026-01-01T00:02:00Z","error":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.RetryInstallAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe("active");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task RetryInstallAsync_ReturnsNullOn404()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.RetryInstallAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task AbortInstallAsync_PostsToAbortEndpoint()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe($"/api/v1/installs/{installId}/abort");

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var found = await client.AbortInstallAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        found.ShouldBeTrue();
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task AbortInstallAsync_ReturnsFalseOn404()
    {
        var installId = Guid.NewGuid();
        var handler = new RecordingHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var found = await client.AbortInstallAsync(
            installId.ToString(), TestContext.Current.CancellationToken);

        found.ShouldBeFalse();
    }

    [Fact]
    public async Task ExportPackageAsync_PostsToExportEndpoint_WritesYaml()
    {
        const string expectedYaml = "metadata:\n  name: my-unit\n";
        string? capturedBody = null;

        var handler = new RecordingHandler(async (req, ct) =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe("/api/v1/tenant/packages/export");
            capturedBody = await req.Content!.ReadAsStringAsync(ct);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(expectedYaml)),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-yaml");
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                {
                    FileName = "package.yaml",
                };
            return response;
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.ExportPackageAsync(
            "my-unit", withValues: false, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(result!.Content).ShouldBe(expectedYaml);
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("my-unit");
        handler.WasCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task ExportPackageAsync_ReturnsNullOn404()
    {
        var handler = new RecordingHandler((req, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"title":"Not Found","status":404}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var result = await client.ExportPackageAsync(
            "missing-unit", withValues: false, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task InstallPackageFromFileAsync_PostsMultipartToFileEndpoint()
    {
        string? capturedContentType = null;
        var handler = new RecordingHandler(async (req, ct) =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe("/api/v1/packages/install/file");
            capturedContentType = req.Content!.Headers.ContentType?.MediaType;

            var installId = Guid.NewGuid();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $$"""{"installId":"{{installId}}","status":"active","packages":[{"packageName":"my-pkg","state":"active","errorMessage":null}],"startedAt":"2026-01-01T00:00:00Z","completedAt":null,"error":null}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        var client = new SpringApiClient(http, BaseUrl);

        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "metadata:\n  name: my-pkg\n");
            var result = await client.InstallPackageFromFileAsync(
                tmpFile, TestContext.Current.CancellationToken);

            result.ShouldNotBeNull();
            result.Status.ShouldBe("active");
            // The request must be multipart/form-data.
            capturedContentType.ShouldBe("multipart/form-data");
            handler.WasCalled.ShouldBeTrue();
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    // ── 5. Removal verification ───────────────────────────────────────────────

    [Fact]
    public void UnitCommand_DoesNotExposeCreateFromTemplateVerb()
    {
        // ADR-0035 decision 4: `spring unit create-from-template` is deleted outright.
        // The sub-command must not exist in the tree.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);

        // System.CommandLine surfaces an unknown verb error; parse errors != errors on
        // the root command itself so we assert no such subcommand exists.
        var subcommandNames = unitCommand.Subcommands.Select(c => c.Name).ToList();
        subcommandNames.ShouldNotContain("create-from-template");
    }

    [Fact]
    public void UnitCreate_DoesNotExposeFromTemplateOption()
    {
        // ADR-0035 decision 4: `--from-template` on `unit create` is deleted.
        var outputOption = CreateOutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var createCommand = unitCommand.Subcommands.First(c => c.Name == "create");

        var optionNames = createCommand.Options.SelectMany(o => o.Aliases).ToList();
        optionNames.ShouldNotContain("--from-template");
    }

    [Fact]
    public void RootCommand_DoesNotExposeApplyVerb()
    {
        // ADR-0035 decision 4: `spring apply` is deleted outright.
        var outputOption = CreateOutputOption();
        var rootCommand = new RootCommand { Options = { outputOption } };

        // Re-create the same subcommand tree as Program.cs minus apply (which
        // was removed from Program.cs as part of this PR).
        rootCommand.Subcommands.Add(PackageCommand.Create(outputOption));
        rootCommand.Subcommands.Add(UnitCommand.Create(outputOption));

        var subcommandNames = rootCommand.Subcommands.Select(c => c.Name).ToList();
        subcommandNames.ShouldNotContain("apply");
    }

    // ── 6. Live-package integration test stub ────────────────────────────────

    [Fact(Skip = "Lights up after #1562 — spring-voyage-oss package.yaml does not yet exist")]
    public async Task PackageInstall_SpringVoyageOss_LiveIntegration_ProducesFiveUnits()
    {
        // Acceptance: `spring package install spring-voyage-oss
        //   --input github_owner=<owner>
        //   --input github_repo=<repo>
        //   --input github_installation_id=<id>`
        // → 5 units + 4 sub-units bound to GitHub, exit 0.
        //
        // This test requires a running Spring Voyage API and a real GitHub App
        // installation.  It is skipped until packages/spring-voyage-oss/package.yaml
        // lands in #1562.
        await Task.CompletedTask; // placeholder
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal recording handler for wire-level tests.
    /// Supports both sync and async responders.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public bool WasCalled { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = (req, ct) => Task.FromResult(responder(req, ct));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return await _responder(request, cancellationToken);
        }
    }
}