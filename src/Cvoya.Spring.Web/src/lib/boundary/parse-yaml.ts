// Client-side YAML parsing for the portal's boundary tab upload mode (#524).
//
// Parity target: `spring unit boundary set -f boundary.yaml`. The CLI
// `ParseBoundaryFromYaml` helper in `UnitBoundaryCommand.cs` consumes
// YamlDotNet's `CamelCaseNamingConvention`, which yields keys like
// `domainPattern` / `overrideLevel`. PR #527 shipped the adjacent
// `spring apply -f` manifest whose boundary block is snake_case
// (`domain_pattern`, `override_level`) courtesy of the manifest's
// `[YamlMember(Alias = ...)]` attributes.
//
// To keep operators from having to remember which verb accepts which
// casing, this parser accepts either form — and also accepts a
// `boundary:` nesting so a dump of a full unit manifest can be pasted
// wholesale. Unknown keys are ignored, matching the tolerance rules in
// `ManifestBoundaryMapper` and the CLI's `IgnoreUnmatchedProperties`
// deserialiser.

import yaml from "js-yaml";

import type {
  BoundaryOpacityRuleDto,
  BoundaryProjectionRuleDto,
  BoundarySynthesisRuleDto,
  UnitBoundaryResponse,
} from "@/lib/api/types";

/** Raised when the YAML is structurally invalid or cannot be projected. */
export class BoundaryYamlParseError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "BoundaryYamlParseError";
  }
}

type MaybeRecord = Record<string, unknown> | null | undefined;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * Pull the first present key out of a record. The CLI's camelCase and the
 * manifest's snake_case are kept intentionally parallel, so accept both.
 */
function pick(
  source: Record<string, unknown>,
  keys: readonly string[],
): unknown {
  for (const key of keys) {
    if (Object.prototype.hasOwnProperty.call(source, key)) {
      return source[key];
    }
  }
  return undefined;
}

function asOptionalString(value: unknown): string | null {
  if (value === null || value === undefined) return null;
  if (typeof value === "string") {
    const trimmed = value.trim();
    return trimmed === "" ? null : trimmed;
  }
  // YAML readers occasionally deliver numbers/booleans where strings are
  // expected (e.g. a level typed as `1`). Coerce rather than reject so a
  // forgiving paste still round-trips.
  return String(value);
}

function asList(value: unknown): unknown[] | null {
  if (value === null || value === undefined) return null;
  if (!Array.isArray(value)) {
    throw new BoundaryYamlParseError(
      "Expected a list of rules but got a single object.",
    );
  }
  return value;
}

function projectOpacity(raw: unknown): BoundaryOpacityRuleDto {
  if (!isRecord(raw)) {
    throw new BoundaryYamlParseError(
      "Each opacity rule must be a mapping (got a scalar or list).",
    );
  }
  return {
    domainPattern: asOptionalString(pick(raw, ["domainPattern", "domain_pattern"])),
    originPattern: asOptionalString(pick(raw, ["originPattern", "origin_pattern"])),
  };
}

function projectProjection(raw: unknown): BoundaryProjectionRuleDto {
  if (!isRecord(raw)) {
    throw new BoundaryYamlParseError(
      "Each projection rule must be a mapping (got a scalar or list).",
    );
  }
  return {
    domainPattern: asOptionalString(pick(raw, ["domainPattern", "domain_pattern"])),
    originPattern: asOptionalString(pick(raw, ["originPattern", "origin_pattern"])),
    renameTo: asOptionalString(pick(raw, ["renameTo", "rename_to"])),
    retag: asOptionalString(pick(raw, ["retag"])),
    overrideLevel: asOptionalString(
      pick(raw, ["overrideLevel", "override_level"]),
    ),
  };
}

function projectSynthesis(raw: unknown): BoundarySynthesisRuleDto | null {
  if (!isRecord(raw)) {
    throw new BoundaryYamlParseError(
      "Each synthesis rule must be a mapping (got a scalar or list).",
    );
  }
  const name = asOptionalString(pick(raw, ["name"]));
  if (!name) {
    // Name is required; silently drop blank-name entries to mirror the CLI
    // and manifest tolerance (a misspelled stub never fabricates a team
    // capability).
    return null;
  }
  return {
    name,
    domainPattern: asOptionalString(pick(raw, ["domainPattern", "domain_pattern"])),
    originPattern: asOptionalString(pick(raw, ["originPattern", "origin_pattern"])),
    description: asOptionalString(pick(raw, ["description"])),
    level: asOptionalString(pick(raw, ["level"])),
  };
}

/**
 * Extract the boundary block out of a YAML document. Accepts two shapes:
 *
 * ```yaml
 * # top-level form (mirrors `spring unit boundary set -f boundary.yaml`)
 * opacities: [...]
 * projections: [...]
 * syntheses: [...]
 * ```
 *
 * ```yaml
 * # nested form (mirrors `spring apply -f unit.yaml` manifests)
 * unit:           # optional wrapper some tools add
 *   boundary:
 *     opacities: [...]
 * # ...or just:
 * boundary:
 *   opacities: [...]
 * ```
 */
function extractBoundaryBlock(doc: MaybeRecord): Record<string, unknown> {
  if (!doc) {
    // Treat an empty / null document as an empty boundary.
    return {};
  }
  // `unit:` wrapper (unit manifest form).
  const unit = pick(doc, ["unit"]);
  if (isRecord(unit)) {
    const nested = pick(unit, ["boundary"]);
    if (isRecord(nested)) return nested;
  }
  // Explicit top-level `boundary:` key.
  const boundary = pick(doc, ["boundary"]);
  if (isRecord(boundary)) return boundary;
  // Bare shape: doc IS the boundary block.
  return doc;
}

/**
 * Parse a YAML string into a {@link UnitBoundaryResponse} suitable for a
 * direct PUT to `/api/v1/units/{id}/boundary`. Throws
 * {@link BoundaryYamlParseError} on any structural problem so the caller
 * can surface an inline error without hitting the server.
 */
export function parseBoundaryYaml(yamlText: string): UnitBoundaryResponse {
  let doc: unknown;
  try {
    doc = yaml.load(yamlText, { schema: yaml.JSON_SCHEMA });
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    throw new BoundaryYamlParseError(`Invalid YAML: ${message}`);
  }

  if (doc !== null && doc !== undefined && !isRecord(doc)) {
    throw new BoundaryYamlParseError(
      "Expected a YAML mapping at the document root.",
    );
  }

  const block = extractBoundaryBlock(doc as MaybeRecord);

  const opacitiesRaw = asList(pick(block, ["opacities"]));
  const projectionsRaw = asList(pick(block, ["projections"]));
  const synthesesRaw = asList(pick(block, ["syntheses"]));

  const opacities = opacitiesRaw?.map(projectOpacity) ?? null;
  const projections = projectionsRaw?.map(projectProjection) ?? null;
  const syntheses =
    synthesesRaw
      ?.map(projectSynthesis)
      .filter((r): r is BoundarySynthesisRuleDto => r !== null) ?? null;

  return {
    opacities: opacities && opacities.length > 0 ? opacities : null,
    projections: projections && projections.length > 0 ? projections : null,
    syntheses: syntheses && syntheses.length > 0 ? syntheses : null,
  };
}
