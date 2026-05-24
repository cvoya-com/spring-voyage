// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json.Serialization;

/// <summary>
/// Content-addressable bundle of workspace files served by the worker's
/// <c>GET /v1/bootstrap/agents/{agentId}</c> endpoint per ADR-0055 §3. The
/// agent-sidecar pulls this bundle on container start and re-checks it on
/// every turn; the platform-authoritative subset (named in
/// <see cref="PlatformFileHashes"/>) is pinned bit-for-bit by the sidecar's
/// per-turn integrity check.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Version"/> and the response <c>ETag</c> are both the sha256 of
/// the bundle's content (computed via <see cref="AgentBootstrapBundleHasher"/>).
/// <see cref="IssuedAt"/> is wallclock-now and intentionally does not
/// participate in the hash — two pulls of the same bundle moments apart
/// must produce identical etags so 304 responses stay cheap.
/// </para>
/// </remarks>
/// <param name="Version">Content hash in the form <c>sha256:&lt;hex&gt;</c>.</param>
/// <param name="IssuedAt">Wallclock time the bundle was assembled.</param>
/// <param name="Files">
/// All files in the bundle, including platform-authoritative and
/// connector-contributed entries. Sorted by <see cref="AgentBootstrapFile.Path"/>
/// for hash determinism — providers MUST emit a sorted list.
/// </param>
/// <param name="PlatformFileHashes">
/// SV-authoritative subset of <see cref="Files"/> — the files the sidecar
/// pins via the per-turn integrity check (ADR-0055 §6). Keys are file paths
/// matching an entry in <see cref="Files"/>; values are the same
/// <c>sha256:&lt;hex&gt;</c> hash as the corresponding
/// <see cref="AgentBootstrapFile.Sha256"/>.
/// </param>
public sealed record AgentBootstrapBundle(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
    [property: JsonPropertyName("files")] IReadOnlyList<AgentBootstrapFile> Files,
    [property: JsonPropertyName("platformFileHashes")] IReadOnlyDictionary<string, string> PlatformFileHashes);

/// <summary>
/// One file in an <see cref="AgentBootstrapBundle"/>.
/// </summary>
/// <param name="Path">
/// Workspace-relative path the sidecar materialises the file at (e.g.
/// <c>.spring/system-prompt.md</c>, <c>.mcp.json</c>, <c>.spring/connectors/&lt;slug&gt;/binding.json</c>).
/// Forward slashes; no leading slash; no <c>..</c> traversal.
/// </param>
/// <param name="Sha256">sha256 of <see cref="Content"/> in the form <c>sha256:&lt;hex&gt;</c>.</param>
/// <param name="Content">
/// UTF-8 file content, inline as a JSON string. Binary payloads are not
/// supported in v0.1 — every platform-emitted file is human-readable
/// (Markdown, JSON, YAML).
/// </param>
public sealed record AgentBootstrapFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("content")] string Content);
