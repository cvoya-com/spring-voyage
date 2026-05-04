/**
 * Role attribution helpers for the conversation thread UI (#410).
 *
 * Conversations are derived from the activity event stream. Each event
 * carries a `source` address. Post-#1629 every wire-form participant
 * address is `scheme:<32-hex-no-dash>` (e.g. `agent:8c5fab2a8e7e…`); the
 * legacy `scheme://path` and `scheme:id:<uuid>` shapes are still tolerated
 * defensively so threads composed of pre-#1629 activity events continue
 * to render.
 *
 * The UI maps the scheme to a small fixed set of presentation roles so
 * visually distinct bubbles stay consistent across the portal.
 */

export type ConversationRole = "human" | "agent" | "unit" | "tool" | "system";

export type AddressKind = "navigation" | "identity";

export interface ParsedThreadSource {
  scheme: string;
  path: string;
  /**
   * The address kind. Post-#1629 every Spring-emitted address is in
   * canonical identity form (`scheme:<hex>`); the navigation form
   * (`scheme://path`) is preserved for legacy events.
   */
  kind: AddressKind;
  /** Original raw source string. */
  raw: string;
}

/**
 * Splits a source address into its components. Accepts every wire form
 * the platform has emitted:
 *
 *   - Identity form `scheme:id:<uuid>` (legacy explicit identity)
 *   - Navigation form `scheme://path` (legacy slug-based)
 *   - Canonical form `scheme:<32-hex-no-dash>` (post-#1629)
 *
 * Falls back to a `system://<raw>` navigation shape when the value
 * doesn't contain a recognised separator — the projection layer can emit
 * shorthand on platform-internal events.
 */
export function parseThreadSource(source: string): ParsedThreadSource {
  // Try explicit identity form first: "scheme:id:<uuid>"
  const idIdx = source.indexOf(":id:");
  if (idIdx > 0) {
    const scheme = source.slice(0, idIdx).toLowerCase();
    const path = source.slice(idIdx + 4);
    // Only treat as identity if the path looks like a UUID (non-empty, no slashes)
    if (path && !path.includes("/") && !path.includes(":")) {
      return { scheme, path, kind: "identity", raw: source };
    }
  }

  // Try navigation form: "scheme://path"
  const navIdx = source.indexOf("://");
  if (navIdx > 0) {
    return {
      scheme: source.slice(0, navIdx).toLowerCase(),
      path: source.slice(navIdx + 3),
      kind: "navigation",
      raw: source,
    };
  }

  // Try canonical post-#1629 form: "scheme:<hex>"
  const colonIdx = source.indexOf(":");
  if (colonIdx > 0 && colonIdx < source.length - 1) {
    const scheme = source.slice(0, colonIdx).toLowerCase();
    const path = source.slice(colonIdx + 1);
    if (path && !path.includes("/") && !path.includes(":")) {
      return { scheme, path, kind: "identity", raw: source };
    }
  }

  // Fallback: no recognisable separator
  return { scheme: "system", path: source, kind: "navigation", raw: source };
}

/**
 * Returns true when the address belongs to the human scheme using the
 * navigation form (`human://`). Humans now also surface in canonical
 * `human:<hex>` form post-#1629; this helper continues to detect the
 * navigation legacy form for back-compat with persisted activity events.
 */
export function isHumanAddress(address: string): boolean {
  return (
    address.startsWith("human://") ||
    address.toLowerCase().startsWith("human:") &&
      !address.startsWith("human://") &&
      address.length > "human:".length
  );
}

/**
 * Loose UUID detector. Returns true for both dashed (`8-4-4-4-12`) and
 * undashed (32 hex chars) forms. Retained because legacy navigation-form
 * paths can still be UUID-shaped (e.g. `agent://<uuid>` from pre-#1629
 * activity events) — the resolver in #1635 covers post-#1629 emit, but
 * this detector still gates the legacy path.
 */
export function looksLikeUuid(value: string): boolean {
  if (!value) return false;
  // Dashed form: 8-4-4-4-12 hex
  if (/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) {
    return true;
  }
  // Undashed form: 32 hex chars
  if (/^[0-9a-f]{32}$/i.test(value)) return true;
  return false;
}

/**
 * Address-shaped value tolerated by the rendering helpers. Mirrors the
 * `ParticipantRef` server shape (`{ address, displayName }`) plus the
 * legacy plain-string form that pre-#1502 servers emit.
 */
export type AddressLike =
  | string
  | { address?: string | null; displayName?: string | null }
  | null
  | undefined;

/**
 * Returns the canonical address string for any `AddressLike` value.
 * Empty string when nothing is available.
 */
export function addressOf(p: AddressLike): string {
  if (!p) return "";
  if (typeof p === "string") return p;
  return p.address ?? "";
}

/**
 * Resolves the human-readable display name for any `AddressLike` value
 * for the engagement portal — never returns a UUID.
 *
 * <p>
 * Post-#1635 the server guarantees that every <c>ParticipantRef</c>
 * returned from the API carries a non-empty <c>displayName</c> (deleted
 * entities surface as <c>&lt;deleted&gt;</c>). This helper therefore
 * collapses to a thin pass-through over the server-supplied value: when
 * the input shape carries a non-empty string we use it verbatim, no
 * UUID-shape filtering applied.
 * </p>
 *
 * <p>
 * The fallbacks below cover the legacy / partial-data cases:
 * </p>
 * <ul>
 *   <li>
 *     The input is a bare string (pre-#1502 server shape): treat the
 *     string as the address and walk the legacy heuristic — this is the
 *     pathway that still gates UUID-shaped paths in case the activity
 *     event is older than #1629.
 *   </li>
 *   <li>
 *     The input is a {@link ParticipantRef} but the server somehow
 *     supplied an empty / whitespace display name. The resolver should
 *     never emit that, but we still cover it with a path-segment fallback
 *     so the UI never blanks the row.
 *   </li>
 * </ul>
 */
export function participantDisplayName(p: AddressLike): string | null {
  if (!p) return null;

  if (typeof p !== "string") {
    // Server-supplied display name is the source of truth post-#1635.
    // Trim and return verbatim (including the `<deleted>` placeholder)
    // when present.
    const dn = p.displayName?.trim();
    if (dn) return dn;
  }

  // Legacy fallback path — the input is either a bare-string address or a
  // ParticipantRef with no displayName. Walk the address with the
  // pre-#1635 heuristic so old activity events (pre-baseline migration)
  // still render rather than leaking a raw GUID.
  const addr = addressOf(p);
  if (!addr) return null;
  const parsed = parseThreadSource(addr);
  if (parsed.kind === "identity") {
    // The path is the raw UUID — never display it.
    return null;
  }
  // Navigation form: refuse UUID-shaped paths and human-scheme paths
  // (which can themselves be UUIDs in v0.x).
  if (parsed.scheme === "human") return null;
  if (looksLikeUuid(parsed.path)) return null;
  return parsed.path || null;
}

/**
 * Resolves the presentation role for a thread event. Tool-call events
 * (`DecisionMade`) get their own role so the thread view can render
 * them as collapsed call-outs (#410 § role attribution).
 */
export function roleFromEvent(
  source: string,
  eventType: string,
): ConversationRole {
  if (eventType === "DecisionMade") {
    return "tool";
  }
  const { scheme } = parseThreadSource(source);
  if (scheme === "human") return "human";
  if (scheme === "agent") return "agent";
  if (scheme === "unit") return "unit";
  return "system";
}

export interface RoleStyle {
  /** Container alignment for the role bubble. */
  align: "start" | "end";
  /** Tailwind classes applied to the bubble container. */
  bubble: string;
  /** Short human-readable label for the role pill. */
  label: string;
}

export const ROLE_STYLES: Record<ConversationRole, RoleStyle> = {
  human: {
    align: "end",
    bubble: "bg-primary text-primary-foreground",
    label: "Human",
  },
  agent: {
    align: "start",
    bubble: "bg-muted text-foreground",
    label: "Agent",
  },
  unit: {
    align: "start",
    bubble: "bg-muted/60 text-foreground",
    label: "Unit",
  },
  tool: {
    align: "start",
    bubble: "bg-amber-50 text-amber-900 border border-amber-200",
    label: "Tool",
  },
  system: {
    align: "start",
    bubble: "bg-muted/40 text-muted-foreground italic",
    label: "System",
  },
};
