// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

/**
 * Builds an AgentPackage YAML manifest from wizard form state (ADR-0035
 * decision 6 — the new-agent wizard constructs a package in memory and
 * submits it through `POST /api/v1/packages/install/file`, the same
 * endpoint the CLI uses).
 *
 * The generated YAML shape:
 * ```yaml
 * apiVersion: spring.cvoya.com/v1
 * kind: AgentPackage
 * metadata:
 *   name: <id>
 *   displayName: <displayName>
 * agent:
 *   id: <id>
 *   name: <displayName>
 *   role: <role>        # omitted when empty
 *   description: <description>  # omitted when empty
 *   execution:
 *     image: <image>    # omitted when empty
 *     runtime: <runtime> # omitted when empty
 *     hosting: <hosting> # omitted when empty
 *   ai:
 *     tool: <tool>      # omitted when empty
 *     model: <model>    # omitted when empty
 * ```
 *
 * Note: the `agent` field in PackageManifest is a string reference
 * (the artefact name). For the scratch wizard we embed the full agent
 * definition inline under an `agent:` block rather than a bare-name
 * reference. The backend install pipeline resolves this via the file-
 * upload path (ADR-0035 decision 13). Full AgentPackage activation is
 * tracked in #1559.
 */

export interface AgentPackageFormState {
  /** URL-safe agent id (becomes both package name and agent name). */
  id: string;
  /** Human-readable label. */
  displayName: string;
  /** Optional role text. */
  role?: string;
  /** Optional description text. */
  description?: string;
  /** Container image reference (`execution.image`). */
  image?: string;
  /** Container runtime key (`execution.runtime`). */
  runtime?: string;
  /** Hosting mode (`execution.hosting`): ephemeral or permanent. */
  hosting?: string;
  /** Execution tool key (`ai.tool`): claude-code, codex, etc. */
  tool?: string;
  /** Model id (`ai.model`). */
  model?: string;
  /**
   * Unit ids the agent should join after install. These are handled as
   * post-install side-effects (sequential membership-add calls) because
   * the AgentPackage schema does not currently support a `members:` block
   * at the agent level. Tracked as a follow-up.
   */
  unitIds: string[];
}

/**
 * Builds the AgentPackage YAML string from wizard form state.
 * Returns a self-contained YAML document ready for upload to
 * `POST /api/v1/packages/install/file`.
 */
export function buildAgentPackageYaml(state: AgentPackageFormState): string {
  const id = state.id.trim();
  const displayName = state.displayName.trim();
  const role = state.role?.trim();
  const description = state.description?.trim();
  const image = state.image?.trim();
  const runtime = state.runtime?.trim();
  const hosting = state.hosting?.trim();
  const tool = state.tool?.trim();
  const model = state.model?.trim();

  const lines: string[] = [
    "apiVersion: spring.cvoya.com/v1",
    "kind: AgentPackage",
    "metadata:",
    `  name: ${yamlScalar(id)}`,
  ];

  if (displayName) {
    lines.push(`  displayName: ${yamlScalar(displayName)}`);
  }

  lines.push("agent:");
  lines.push(`  id: ${yamlScalar(id)}`);
  lines.push(`  name: ${yamlScalar(displayName)}`);

  if (role) {
    lines.push(`  role: ${yamlScalar(role)}`);
  }

  if (description) {
    lines.push(`  description: ${yamlScalar(description)}`);
  }

  // Execution block
  const hasExecution = image || runtime || hosting;
  if (hasExecution) {
    lines.push("  execution:");
    if (image) lines.push(`    image: ${yamlScalar(image)}`);
    if (runtime) lines.push(`    runtime: ${yamlScalar(runtime)}`);
    if (hosting) lines.push(`    hosting: ${yamlScalar(hosting)}`);
  }

  // AI block
  const hasAi = tool || model;
  if (hasAi) {
    lines.push("  ai:");
    if (tool) lines.push(`    tool: ${yamlScalar(tool)}`);
    if (model) lines.push(`    model: ${yamlScalar(model)}`);
  }

  return lines.join("\n") + "\n";
}

/**
 * Quotes a YAML scalar value when it contains characters that require
 * quoting. Uses double-quote style so standard escape sequences work.
 * Sufficient for operator-supplied identifiers and human-readable strings.
 *
 * YAML quoting rules for scalar values (not keys):
 * - A colon is only problematic when followed by a space (`: `), so
 *   bare URLs like `ghcr.io/example:latest` do not need quoting.
 * - Other indicators (#, &, *, {, }, [, ], |, >, ', ", %, @, `) are unsafe.
 * - Leading/trailing whitespace requires quoting.
 * - Leading `-`, `?`, `:` require quoting.
 */
function yamlScalar(value: string): string {
  const needsQuoting =
    /[#,{}\[\]&*!|>'"%@`\r\n\t]/.test(value) ||
    /: /.test(value) ||        // colon-space sequence
    /^[-?:]/.test(value) ||    // leading indicator character
    /^\s/.test(value) ||        // leading whitespace
    /\s$/.test(value);          // trailing whitespace

  if (!needsQuoting) {
    return value;
  }
  // Escape backslashes and double-quotes, then wrap.
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}
