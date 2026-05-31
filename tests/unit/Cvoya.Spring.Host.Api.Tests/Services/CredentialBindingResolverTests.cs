// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Collections.Generic;
using System.IO;
using System.Linq;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Pure-resolver tests for <see cref="CredentialBindingResolver"/> (#2159).
/// Tenant-scope inheritance is covered by <see cref="PackageInstallServiceCredentialPreflightTests"/>;
/// these tests pin the runtime-catalogue derivation + match-against-supplied
/// behaviour with no DB or async dependencies in the way.
/// </summary>
public class CredentialBindingResolverTests
{
    private static readonly IRuntimeCatalog Catalog =
        Cvoya.Spring.RuntimeCatalog.RuntimeCatalogLoader.LoadEmbedded();

    [Fact]
    public void CollectRequired_DerivesAnthropicOauthEdge_ForClaudeCode()
    {
        var pkg = MakePackage(("alpha", "claude-code", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.Count.ShouldBe(1);
        var entry = required.Single();
        entry.Provider.ShouldBe("anthropic");
        entry.AuthMethod.ShouldBe(AuthMethod.Oauth);
        entry.SecretName.ShouldBe("anthropic-oauth");
        entry.CredentialEnvVar.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        entry.ConsumingUnits.ShouldBe(new[] { "alpha" });
    }

    [Fact]
    public void CollectRequired_DerivesAnthropicApiKeyEdge_ForSpringVoyage()
    {
        var pkg = MakePackage(("beta", "spring-voyage", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.Single().AuthMethod.ShouldBe(AuthMethod.ApiKey);
        required.Single().SecretName.ShouldBe("anthropic-api-key");
        required.Single().CredentialEnvVar.ShouldBe("ANTHROPIC_API_KEY");
    }

    [Fact]
    public void CollectRequired_SkipsEdgesWithNoCredential()
    {
        // spring-voyage + ollama declares authMethod: null in the catalog.
        var pkg = MakePackage(("gamma", "spring-voyage", "ollama"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.ShouldBeEmpty();
    }

    [Fact]
    public void CollectRequired_DeduplicatesEdges_AcrossUnits()
    {
        var pkg = MakePackage(
            ("one", "claude-code", "anthropic"),
            ("two", "claude-code", "anthropic"),
            ("three", "claude-code", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.Count.ShouldBe(1);
        required.Single().ConsumingUnits.ShouldBe(new[] { "one", "two", "three" });
    }

    [Fact]
    public void CollectRequired_KeepsDistinctEdges_OnSameProvider()
    {
        // claude-code → anthropic uses oauth, spring-voyage → anthropic uses api-key.
        // Both should appear as distinct required entries.
        var pkg = MakePackage(
            ("oauth-unit", "claude-code", "anthropic"),
            ("api-key-unit", "spring-voyage", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.Count.ShouldBe(2);
        required.ShouldContain(r => r.AuthMethod == AuthMethod.Oauth);
        required.ShouldContain(r => r.AuthMethod == AuthMethod.ApiKey);
    }

    [Fact]
    public void Resolve_MatchesSuppliedBindings_AndReturnsUnsuppliedCandidates()
    {
        var pkg = MakePackage(
            ("a", "claude-code", "anthropic"),
            ("b", "spring-voyage", "openai"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);
        var supplied = new List<CredentialBinding>
        {
            new("anthropic", AuthMethod.Oauth, "sk-ant-oat-xxx"),
        };

        var resolution = CredentialBindingResolver.Resolve(required, supplied);

        resolution.Resolved.Count.ShouldBe(1);
        resolution.Resolved.Single().Required.Provider.ShouldBe("anthropic");
        resolution.UnsuppliedCandidates.Count.ShouldBe(1);
        resolution.UnsuppliedCandidates.Single().Provider.ShouldBe("openai");
        resolution.UnknownEdges.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_FlagsSuppliedBindings_NotConsumedByAnyUnit()
    {
        // Package declares only claude-code+anthropic but operator
        // supplied an openai key. The resolver flags that as an
        // unknown edge so the endpoint can return 400.
        var pkg = MakePackage(("a", "claude-code", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);
        var supplied = new List<CredentialBinding>
        {
            new("openai", AuthMethod.ApiKey, "sk-..."),
        };

        var resolution = CredentialBindingResolver.Resolve(required, supplied);

        resolution.UnknownEdges.Count.ShouldBe(1);
        resolution.UnknownEdges.Single().Provider.ShouldBe("openai");
        resolution.UnknownEdges.Single().AuthMethod.ShouldBe(AuthMethod.ApiKey);
    }

    [Fact]
    public void Resolve_NormalisesProviderCase_OnSuppliedBindings()
    {
        var pkg = MakePackage(("a", "claude-code", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);
        var supplied = new List<CredentialBinding>
        {
            new("Anthropic", AuthMethod.Oauth, "sk-ant-oat-xxx"),
        };

        var resolution = CredentialBindingResolver.Resolve(required, supplied);

        resolution.Resolved.Count.ShouldBe(1);
        resolution.UnknownEdges.ShouldBeEmpty();
    }

    [Fact]
    public void ToMissing_PreservesProviderAuthMethodAndConsumers()
    {
        var pkg = MakePackage(
            ("u1", "claude-code", "anthropic"),
            ("u2", "claude-code", "anthropic"));
        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog).Single();

        var missing = CredentialBindingResolver.ToMissing(required);

        missing.Provider.ShouldBe("anthropic");
        missing.AuthMethod.ShouldBe(AuthMethod.Oauth);
        missing.SecretName.ShouldBe("anthropic-oauth");
        missing.CredentialEnvVar.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        missing.Scope.ShouldBe("package");
        missing.UnitName.ShouldBeNull();
        missing.ConsumingUnits.ShouldBe(new[] { "u1", "u2" });
    }

    // Regression: a unit that declares a STRUCTURED `requires:` (connector +
    // labels) and inline-stamp `members:` — the Spring Voyage OSS shape —
    // must still surface its runtime credential. The previous derivation
    // deserialised the whole UnitManifest (walking `requires:`/`members:`)
    // without the RequirementEntry converter, so it threw and silently
    // dropped the unit to no-credential. The fix reads only the `ai:` block.
    [Fact]
    public void CollectRequired_DerivesCredential_WhenUnitHasStructuredRequiresAndInlineMembers()
    {
        const string content =
            "apiVersion: spring.voyage/v1\n" +
            "kind: Unit\n" +
            "name: oss-like\n" +
            "ai:\n" +
            "  runtime: claude-code\n" +
            "  model:\n" +
            "    provider: anthropic\n" +
            "    id: claude-opus-4-7\n" +
            "members:\n" +
            "  - agent: { name: ada, from: software-engineer, displayName: \"Ada\" }\n" +
            "  - human:\n" +
            "      roles: [overall-lead]\n" +
            "requires:\n" +
            "  - connector: github\n" +
            "    labels:\n" +
            "      include:\n" +
            "        - spring-voyage-team\n";

        var pkg = new ResolvedPackage
        {
            Name = "test",
            Kind = PackageKind.UnitPackage,
            InputValues = new Dictionary<string, string>(),
            Units = new[]
            {
                new ResolvedArtefact
                {
                    Name = "oss-like",
                    Kind = ArtefactKind.Unit,
                    ResolvedPath = Path.Combine(Path.GetTempPath(), "oss-like.yaml"),
                    Content = content,
                },
            },
            Agents = System.Array.Empty<ResolvedArtefact>(),
            Skills = System.Array.Empty<ResolvedArtefact>(),
            HumanTemplates = System.Array.Empty<ResolvedArtefact>(),
        };

        var required = CredentialBindingResolver.CollectRequired(pkg, Catalog);

        required.Count.ShouldBe(1);
        required.Single().Provider.ShouldBe("anthropic");
        required.Single().AuthMethod.ShouldBe(AuthMethod.Oauth);
        required.Single().CredentialEnvVar.ShouldBe("CLAUDE_CODE_OAUTH_TOKEN");
        required.Single().ConsumingUnits.ShouldBe(new[] { "oss-like" });
    }

    private static ResolvedPackage MakePackage(params (string Name, string Runtime, string Provider)[] units)
    {
        var artefacts = units
            .Select(u => new ResolvedArtefact
            {
                Name = u.Name,
                Kind = ArtefactKind.Unit,
                ResolvedPath = Path.Combine(Path.GetTempPath(), $"{u.Name}.yaml"),
                Content =
                    $"apiVersion: spring.voyage/v1\nkind: Unit\nname: {u.Name}\n" +
                    $"ai:\n  runtime: {u.Runtime}\n  model:\n    provider: {u.Provider}\n    id: x\n",
            })
            .ToList();

        return new ResolvedPackage
        {
            Name = "test",
            Kind = PackageKind.UnitPackage,
            InputValues = new Dictionary<string, string>(),
            Units = artefacts,
            Agents = System.Array.Empty<ResolvedArtefact>(),
            Skills = System.Array.Empty<ResolvedArtefact>(),
            HumanTemplates = System.Array.Empty<ResolvedArtefact>(),
        };
    }
}
