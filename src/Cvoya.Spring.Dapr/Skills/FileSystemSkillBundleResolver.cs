// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// File-system backed <see cref="ISkillBundleResolver"/>. Reads bundle prompts
/// from <c>{PackagesRoot}/{package-dir}/skills/{skill}.md</c> and companion
/// tool schemas from <c>{skill}.tools.json</c>. Missing <c>.tools.json</c> is
/// tolerated: the resulting bundle has an empty
/// <see cref="SkillBundle.RequiredTools"/> list.
/// </summary>
/// <remarks>
/// <para>
/// <b>Package-name resolution.</b> A manifest writes the canonical form
/// <c>spring-voyage/software-engineering</c>, but the repo layout uses
/// <c>packages/software-engineering/</c>. The resolver strips any namespace
/// prefix listed in <see cref="SkillBundleOptions.NamespacePrefixes"/>
/// (<c>spring-voyage/</c> by default) before looking up the directory; an
/// unprefixed name is used as-is.
/// </para>
/// <para>
/// <b>Caching.</b> Bundles are cached in-memory after the first successful
/// resolve. The cache is restart-scoped — no mtime polling, no inotify — so
/// operators updating bundles on disk must restart the host. This matches the
/// "simpler; restart-only is fine for OSS" note in the C4 design.
/// </para>
/// </remarks>
public class FileSystemSkillBundleResolver : ISkillBundleResolver
{
    private readonly SkillBundleOptions _options;
    private readonly ILogger<FileSystemSkillBundleResolver> _logger;
    private readonly ConcurrentDictionary<string, SkillBundle> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new <see cref="FileSystemSkillBundleResolver"/>.
    /// </summary>
    public FileSystemSkillBundleResolver(
        IOptions<SkillBundleOptions> options,
        ILogger<FileSystemSkillBundleResolver> logger)
        : this(options.Value, logger)
    {
    }

    /// <summary>
    /// Overload that accepts a pre-built <see cref="SkillBundleOptions"/>
    /// value. Kept public so tests can inject a fixture root without going
    /// through <see cref="IOptions{T}"/>.
    /// </summary>
    public FileSystemSkillBundleResolver(
        SkillBundleOptions options,
        ILogger<FileSystemSkillBundleResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SkillBundle> ResolveAsync(
        SkillBundleReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var cacheKey = $"{reference.Package}|{reference.Skill}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var (packageDir, packageRoot) = ResolvePackageDirectory(reference.Package);
        var skillsDir = Path.Combine(packageDir, "skills");
        var promptPath = Path.Combine(skillsDir, reference.Skill + ".md");
        if (!File.Exists(promptPath))
        {
            throw new SkillBundleNotFoundException(reference.Package, reference.Skill, promptPath);
        }

        var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken).ConfigureAwait(false);

        var toolsPath = Path.Combine(skillsDir, reference.Skill + ".tools.json");
        IReadOnlyList<SkillToolRequirement> requirements;
        if (File.Exists(toolsPath))
        {
            var toolsJson = await File.ReadAllTextAsync(toolsPath, cancellationToken).ConfigureAwait(false);
            requirements = ParseToolsJson(reference, toolsJson);
        }
        else
        {
            _logger.LogDebug(
                "No '{ToolsFile}' for bundle '{Package}/{Skill}'; treating as prompt-only (empty tool list).",
                toolsPath, reference.Package, reference.Skill);
            requirements = Array.Empty<SkillToolRequirement>();
        }

        var bundle = new SkillBundle(
            PackageName: reference.Package,
            SkillName: reference.Skill,
            Prompt: prompt,
            RequiredTools: requirements);

        _cache.TryAdd(cacheKey, bundle);

        _logger.LogInformation(
            "Resolved skill bundle '{Package}/{Skill}' with {ToolCount} required tool(s) from '{PackageRoot}'.",
            reference.Package, reference.Skill, requirements.Count, packageRoot);

        return bundle;
    }

    private (string PackageDir, string PackageRoot) ResolvePackageDirectory(string packageName)
    {
        var root = _options.PackagesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new SkillBundlePackageNotFoundException(
                packageName,
                "(no packages root configured — set 'Skills:PackagesRoot', 'Packages:Root', or provide SkillBundleOptions.PackagesRoot)");
        }

        if (!Directory.Exists(root))
        {
            throw new SkillBundlePackageNotFoundException(packageName, root);
        }

        var directoryName = NormalisePackageName(packageName);
        if (ContainsTraversal(directoryName))
        {
            // Safety: refuse traversal in the package segment. The manifest
            // layer owns the grammar, but we don't trust it with file paths.
            throw new SkillBundlePackageNotFoundException(packageName, root);
        }

        var candidate = Path.Combine(root, directoryName);
        if (!Directory.Exists(candidate))
        {
            throw new SkillBundlePackageNotFoundException(packageName, candidate);
        }

        // Reject resolved paths that escape the packages root — defence in
        // depth against odd NamespacePrefix configurations.
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            throw new SkillBundlePackageNotFoundException(packageName, candidate);
        }

        return (candidate, root);
    }

    private string NormalisePackageName(string packageName)
    {
        foreach (var prefix in _options.NamespacePrefixes)
        {
            if (!string.IsNullOrEmpty(prefix)
                && packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return packageName.Substring(prefix.Length);
            }
        }
        return packageName;
    }

    private static bool ContainsTraversal(string segment) =>
        string.IsNullOrWhiteSpace(segment)
        || segment.Contains("..", StringComparison.Ordinal)
        || segment.Contains('/', StringComparison.Ordinal)
        || segment.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// Parses a <c>{skill}.tools.json</c> document into
    /// <see cref="SkillToolRequirement"/> entries. The on-disk format mirrors
    /// existing samples: an array of objects with <c>name</c>,
    /// <c>description</c>, and a JSON-schema <c>parameters</c> object.
    /// Accepts both <c>parameters</c> and <c>inputSchema</c> key names so the
    /// file-system layout can evolve without breaking the resolver.
    /// </summary>
    private static IReadOnlyList<SkillToolRequirement> ParseToolsJson(
        SkillBundleReference reference,
        string toolsJson)
    {
        using var doc = JsonDocument.Parse(toolsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new SpringException(
                $"Bundle '{reference.Package}/{reference.Skill}' tools.json must be a JSON array.");
        }

        var list = new List<SkillToolRequirement>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new SpringException(
                    $"Bundle '{reference.Package}/{reference.Skill}' tools.json entries must be objects.");
            }

            if (!item.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
            {
                throw new SpringException(
                    $"Bundle '{reference.Package}/{reference.Skill}' tools.json entry is missing a string 'name'.");
            }

            var description = item.TryGetProperty("description", out var descProp)
                && descProp.ValueKind == JsonValueKind.String
                    ? descProp.GetString() ?? string.Empty
                    : string.Empty;

            JsonElement schema;
            if (item.TryGetProperty("parameters", out var paramProp))
            {
                schema = paramProp.Clone();
            }
            else if (item.TryGetProperty("inputSchema", out var inputProp))
            {
                schema = inputProp.Clone();
            }
            else
            {
                // Empty object schema is a valid "no parameters" signal.
                using var emptyDoc = JsonDocument.Parse("{}");
                schema = emptyDoc.RootElement.Clone();
            }

            var optional = item.TryGetProperty("optional", out var optProp)
                && optProp.ValueKind == JsonValueKind.True;

            list.Add(new SkillToolRequirement(
                Name: nameProp.GetString()!,
                Description: description,
                Schema: schema,
                Optional: optional));
        }

        return list;
    }
}