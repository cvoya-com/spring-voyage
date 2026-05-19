// Schema-driven form rendering for the user-identity surface
// (ADR-0047 §4). Each connector contributes a JSON Schema describing
// the display-identity fields its `(tenant_user, connector)` row holds.
// For v0.1 only GitHub ships one (`{ username, display_handle? }`); the
// renderer below is generic so a second connector slots in without
// per-connector code.

export interface UserConfigField {
  /**
   * Property name as it appears in the schema's `properties` map
   * (e.g. `"username"`, `"display_handle"`). The wire field on
   * `TenantUserConnectorIdentityRequest` is `username` /
   * `displayHandle`; the renderer maps `display_handle` (the schema
   * convention) onto the camelCase request field — see consumers.
   */
  name: string;
  /** Human-readable label rendered above the input. */
  label: string;
  /** Optional helper copy rendered below the input. */
  description?: string;
  /** Whether the field is required (drives validation gating). */
  required: boolean;
  /** Whether the schema declared the field nullable / optional. */
  nullable: boolean;
}

/**
 * Parses a connector user-config schema (`GET /api/v1/tenant/connectors/
 * {slug}/user-config-schema`) into a normalised field list the
 * `<ConnectorIdentityCard>` renders form rows from. Tolerant of missing
 * fields and unknown property orderings — any field whose `type` is not
 * a string variant is filtered out (v0.1's display-identity surface is
 * strictly string-valued).
 */
export function parseUserConfigSchema(value: unknown): UserConfigField[] {
  if (value === null || typeof value !== "object") return [];
  const root = value as Record<string, unknown>;
  const properties = root.properties;
  if (properties === null || typeof properties !== "object") return [];

  const requiredSet = new Set<string>();
  if (Array.isArray(root.required)) {
    for (const name of root.required) {
      if (typeof name === "string") requiredSet.add(name);
    }
  }

  const fields: UserConfigField[] = [];
  for (const [name, rawSpec] of Object.entries(
    properties as Record<string, unknown>,
  )) {
    if (rawSpec === null || typeof rawSpec !== "object") continue;
    const spec = rawSpec as Record<string, unknown>;
    const type = spec.type;
    const isStringLike =
      type === "string" ||
      (Array.isArray(type) &&
        type.includes("string") &&
        type.every((t) => t === "string" || t === "null"));
    if (!isStringLike) continue;
    const nullable =
      Array.isArray(type) && type.includes("null") ? true : false;
    fields.push({
      name,
      label: humaniseLabel(name),
      description:
        typeof spec.description === "string" ? spec.description : undefined,
      required: requiredSet.has(name),
      nullable,
    });
  }
  return fields;
}

function humaniseLabel(name: string): string {
  return name
    .replace(/[_-]+/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

/**
 * Maps a schema field name (snake_case as declared by the connector) to
 * the corresponding camelCase property on
 * `TenantUserConnectorIdentityRequest`. The wire request only has
 * `username` and `displayHandle`; future connectors that declare
 * additional fields will need this map extended alongside the wire
 * schema change.
 */
export function schemaFieldToRequestKey(name: string): "username" | "displayHandle" | null {
  if (name === "username") return "username";
  if (name === "display_handle") return "displayHandle";
  return null;
}

/**
 * Read the existing identity row's value for a schema field. Mirrors
 * `schemaFieldToRequestKey` in reverse.
 */
export function readIdentityField(
  identity: { username: string; displayHandle: null | string } | null,
  schemaFieldName: string,
): string {
  if (identity === null) return "";
  if (schemaFieldName === "username") return identity.username;
  if (schemaFieldName === "display_handle") {
    return identity.displayHandle ?? "";
  }
  return "";
}
