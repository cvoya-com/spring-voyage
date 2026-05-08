// ADR-0039 §6 — multi-parent execution-config inheritance conflict.
//
// When an agent is assigned to a unit whose post-assignment parent set
// disagrees on an inherited execution-config field (e.g. `runtime`,
// `model.provider`), the API returns 422 with a structured problem-details
// body that names every diverging field and the parent unit that
// contributed each value. The same shape is emitted by the agent-create
// endpoint, every membership-add endpoint, and the execution-update
// endpoint (B1–B6 in the ADR-0039 plan).
//
// Wire shape (from `MultiParentInheritanceProblems.cs` /
// `UnitEndpoints.BuildMultiParentInheritanceConflictBody`):
//
// ```json
// {
//   "error": "MultiParentInheritanceConflict",
//   "conflictingFields": {
//     "runtime": [
//       { "source": "<unitId>", "value": "claude-code" },
//       { "source": "<unitId>", "value": "spring-voyage" }
//     ]
//   }
// }
// ```
//
// Some endpoints emit `unitId` instead of `source`; this helper accepts
// both so the portal renders the structured 422 regardless of which API
// surface raised it.

/**
 * One parent unit's contribution to a conflicting field, as projected
 * from the resolver onto the wire body. `unitId` is the canonical
 * 32-character no-dash hex form of the parent's GUID; `value` is the
 * raw field value (string, since every conflicting field flattens to a
 * string at the wire boundary).
 */
export interface ConflictParentValue {
  /** Canonical 32-character no-dash hex form of the parent unit's id. */
  unitId: string;
  /** The field value the parent unit contributed. */
  value: string;
}

/**
 * Parsed multi-parent inheritance conflict — one entry per diverging
 * field, in insertion order. Stable order makes the inline error block
 * deterministic for screenshot regression.
 */
export interface MultiParentInheritanceConflict {
  /** Per-field map of the diverging values. */
  fields: Array<{
    field: string;
    values: ConflictParentValue[];
  }>;
}

/**
 * Try to parse a multi-parent inheritance conflict from an arbitrary
 * value (typically `ApiError.body`). Returns `null` if the shape does
 * not match — the caller falls back to its generic error path.
 *
 * Accepts both `source` and `unitId` keys on the per-parent objects so
 * the helper survives either API surface.
 */
export function parseMultiParentInheritanceConflict(
  body: unknown,
): MultiParentInheritanceConflict | null {
  // Bodies sometimes arrive as strings (raw text bodies from non-2xx
  // responses parsed before their content-type was inspected). Try to
  // JSON-parse a string body once before failing.
  let candidate: unknown = body;
  if (typeof candidate === "string") {
    try {
      candidate = JSON.parse(candidate);
    } catch {
      return null;
    }
  }

  if (!candidate || typeof candidate !== "object") return null;
  const root = candidate as Record<string, unknown>;
  if (root.error !== "MultiParentInheritanceConflict") return null;

  const raw = root.conflictingFields;
  if (!raw || typeof raw !== "object") return null;

  const fields: MultiParentInheritanceConflict["fields"] = [];
  for (const [field, list] of Object.entries(raw as Record<string, unknown>)) {
    if (!Array.isArray(list)) continue;
    const values: ConflictParentValue[] = [];
    for (const entry of list) {
      if (!entry || typeof entry !== "object") continue;
      const obj = entry as Record<string, unknown>;
      // Accept both `source` and `unitId` — different API endpoints
      // have shipped the same shape with the two key names.
      const unitIdRaw = obj.unitId ?? obj.source;
      const valueRaw = obj.value;
      if (typeof unitIdRaw !== "string" || typeof valueRaw !== "string") continue;
      values.push({ unitId: unitIdRaw, value: valueRaw });
    }
    if (values.length > 0) fields.push({ field, values });
  }

  if (fields.length === 0) return null;
  return { fields };
}

/**
 * Normalise a unit id to the canonical 32-character no-dash lowercase
 * hex form used in the conflict body. Accepts hyphenated GUIDs (the
 * default `Guid.ToString()` form returned by `GET /units` responses)
 * and already-canonical strings. Returns `null` when the input does
 * not look like a GUID at all.
 *
 * Used by the renderer to map a conflict's `unitId` back onto the
 * unit list the form already loaded so we can display a friendly name
 * alongside the canonical id.
 */
export function canonicalUnitId(value: string): string | null {
  const trimmed = value.trim().replace(/-/g, "").toLowerCase();
  if (trimmed.length !== 32) return null;
  if (!/^[0-9a-f]{32}$/.test(trimmed)) return null;
  return trimmed;
}
