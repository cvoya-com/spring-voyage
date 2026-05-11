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
 * Address-shaped value tolerated by the rendering helpers. Mirrors the
 * `ParticipantRef` server shape (`{ id, address, displayName }`) plus the
 * legacy plain-string form that pre-#1502 servers emit.
 *
 * Post-#2082: identity comparisons must use {@link idOf} (the participant's
 * stable Guid). {@link addressOf} is for display / routing only.
 */
export type AddressLike =
  | string
  | { id?: string | null; address?: string | null; displayName?: string | null }
  | null
  | undefined;

/**
 * Returns the canonical address string for any `AddressLike` value.
 * Empty string when nothing is available.
 *
 * Use for **display / routing only**. Identity comparisons (e.g. "is this
 * me?") must use {@link idOf} — addresses are a presentation primitive and
 * may legitimately surface in more than one shape across the wire (#2082).
 */
export function addressOf(p: AddressLike): string {
  if (!p) return "";
  if (typeof p === "string") return p;
  return p.address ?? "";
}

/**
 * Returns the stable Guid identity (lowercased, dashed string) carried by
 * a `ParticipantRef`-shaped value, or `null` when the value is a plain
 * address string or the server omitted the id field.
 *
 * Identity comparisons across `ParticipantRef`s — and between a participant
 * and the authenticated user — should be done on this primitive, not on
 * `addressOf(p)`. The address-string form historically drifted between
 * `scheme:<hex>` and `scheme:id:<hex>` rendering, causing string-equality
 * checks to silently miss matches (#2082).
 */
export function idOf(p: AddressLike): string | null {
  if (!p || typeof p === "string") return null;
  const raw = p.id?.trim().toLowerCase();
  return raw ? raw : null;
}

/**
 * True when two `AddressLike` values refer to the same actor — i.e. carry
 * the same {@link idOf} primitive. Returns `false` when either side has no
 * resolvable identity (string-only participants from legacy servers, or
 * absent values); callers needing tri-state behaviour should use `idOf`
 * directly.
 */
export function sameIdentity(a: AddressLike, b: AddressLike): boolean {
  const ida = idOf(a);
  const idb = idOf(b);
  return ida !== null && idb !== null && ida === idb;
}

/**
 * Resolves the human-readable display name for any `AddressLike` value
 * for the engagement portal.
 *
 * <p>
 * Post-#1635 (PR #1643) the server guarantees that every
 * <c>ParticipantRef</c> returned from the API carries a non-empty
 * <c>displayName</c> (deleted entities surface as <c>&lt;deleted&gt;</c>).
 * This helper is a thin pass-through over the server-supplied value:
 * trimmed, returned verbatim when present, otherwise <c>null</c>. Callers
 * surface their own "Unknown participant" fallback when the resolver
 * returns <c>null</c>.
 * </p>
 *
 * <p>
 * The legacy navigation-form / identity-form filtering heuristic that
 * lived here pre-#1645 has been removed. If a raw GUID ever leaks into
 * the UI again, that's a server-side resolver bug (#1635), not something
 * the portal should mask.
 * </p>
 */
export function participantDisplayName(p: AddressLike): string | null {
  if (!p || typeof p === "string") return null;
  const dn = p.displayName?.trim();
  return dn ? dn : null;
}

/**
 * Returns the actor's runtime-status kind (`"agent"` / `"unit"`) when
 * the participant resolves to one of those schemes, or `null` for
 * humans / system / tool / unknown shapes (#2100). Used to decide
 * whether to render a `<RuntimeStatusBadge>` next to a participant's
 * name — humans don't have a runtime-status indicator and tool /
 * system rows are presentation-only.
 */
export function runtimeKindOf(p: AddressLike): "agent" | "unit" | null {
  if (!p) return null;
  const addr = typeof p === "string" ? p : p.address ?? "";
  if (!addr) return null;
  const { scheme } = parseThreadSource(addr);
  if (scheme === "agent") return "agent";
  if (scheme === "unit") return "unit";
  return null;
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
