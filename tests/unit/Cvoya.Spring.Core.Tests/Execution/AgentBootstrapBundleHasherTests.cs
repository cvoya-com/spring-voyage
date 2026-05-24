// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Execution;

using Cvoya.Spring.Core.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentBootstrapBundleHasher"/>. The hash drives
/// the ETag-based 304 path on the bootstrap endpoint, so the canonicalisation
/// must be byte-stable across processes and the input ordering must produce
/// a single hash regardless of caller iteration order.
/// </summary>
public class AgentBootstrapBundleHasherTests
{
    [Fact]
    public void Compute_FormatsAsSha256PrefixedLowercaseHex()
    {
        var files = new[] { new AgentBootstrapFile("a", "sha256:0", "x") };
        var hashes = new Dictionary<string, string>();

        var result = AgentBootstrapBundleHasher.Compute(files, hashes);

        result.ShouldStartWith("sha256:");
        var hex = result["sha256:".Length..];
        hex.Length.ShouldBe(64);
        hex.ShouldMatch("^[0-9a-f]+$");
    }

    [Fact]
    public void Compute_DeterministicForSameInputs()
    {
        var files = SampleFiles();
        var hashes = SampleHashes();

        var first = AgentBootstrapBundleHasher.Compute(files, hashes);
        var second = AgentBootstrapBundleHasher.Compute(files, hashes);

        first.ShouldBe(second);
    }

    [Fact]
    public void Compute_FileOrderingDoesNotAffectHash()
    {
        // The hasher canonicalises file ordering by sorting on Path. Two
        // callers that emit files in different orders MUST get the same hash.
        var ordered = SampleFiles();
        var reversed = ordered.AsEnumerable().Reverse().ToList();
        var hashes = SampleHashes();

        var hashOrdered = AgentBootstrapBundleHasher.Compute(ordered, hashes);
        var hashReversed = AgentBootstrapBundleHasher.Compute(reversed, hashes);

        hashOrdered.ShouldBe(hashReversed);
    }

    [Fact]
    public void Compute_PlatformFileHashesOrderingDoesNotAffectHash()
    {
        var files = SampleFiles();

        var hashesA = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".spring/system-prompt.md"] = "sha256:aaaa",
            [".mcp.json"] = "sha256:bbbb",
        };
        var hashesB = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [".mcp.json"] = "sha256:bbbb",
            [".spring/system-prompt.md"] = "sha256:aaaa",
        };

        var hashA = AgentBootstrapBundleHasher.Compute(files, hashesA);
        var hashB = AgentBootstrapBundleHasher.Compute(files, hashesB);

        hashA.ShouldBe(hashB);
    }

    [Fact]
    public void Compute_ContentChange_ChangesHash()
    {
        var hashes = SampleHashes();
        var original = SampleFiles();
        var modified = original.Select((f, i) =>
            i == 0 ? f with { Content = f.Content + "-changed" } : f).ToList();

        AgentBootstrapBundleHasher.Compute(original, hashes)
            .ShouldNotBe(AgentBootstrapBundleHasher.Compute(modified, hashes));
    }

    [Fact]
    public void Compute_PathChange_ChangesHash()
    {
        var hashes = SampleHashes();
        var original = SampleFiles();
        var modified = original.Select((f, i) =>
            i == 0 ? f with { Path = f.Path + "-renamed" } : f).ToList();

        AgentBootstrapBundleHasher.Compute(original, hashes)
            .ShouldNotBe(AgentBootstrapBundleHasher.Compute(modified, hashes));
    }

    [Fact]
    public void Compute_PlatformHashChange_ChangesHash()
    {
        var files = SampleFiles();
        var baseline = SampleHashes();
        var changed = new Dictionary<string, string>(baseline, StringComparer.Ordinal)
        {
            [".spring/system-prompt.md"] = "sha256:different",
        };

        AgentBootstrapBundleHasher.Compute(files, baseline)
            .ShouldNotBe(AgentBootstrapBundleHasher.Compute(files, changed));
    }

    [Fact]
    public void ComputeFileHash_DeterministicAndPrefixed()
    {
        var hash = AgentBootstrapBundleHasher.ComputeFileHash("hello");

        hash.ShouldBe(AgentBootstrapBundleHasher.ComputeFileHash("hello"));
        hash.ShouldStartWith("sha256:");
        hash["sha256:".Length..].Length.ShouldBe(64);
    }

    [Fact]
    public void ComputeFileHash_DifferentContent_DifferentHash()
    {
        AgentBootstrapBundleHasher.ComputeFileHash("hello")
            .ShouldNotBe(AgentBootstrapBundleHasher.ComputeFileHash("world"));
    }

    private static List<AgentBootstrapFile> SampleFiles() => new()
    {
        new(".spring/system-prompt.md", "sha256:aaaa", "You are an agent."),
        new(".mcp.json", "sha256:bbbb", "{\"mcpServers\":{}}"),
        new("context/tenant-config.json", "sha256:cccc", "{\"tenant_id\":\"t\"}"),
    };

    private static Dictionary<string, string> SampleHashes() => new(StringComparer.Ordinal)
    {
        [".spring/system-prompt.md"] = "sha256:aaaa",
        [".mcp.json"] = "sha256:bbbb",
    };
}
