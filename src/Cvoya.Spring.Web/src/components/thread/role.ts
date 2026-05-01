/**
 * Role attribution helpers for the conversation thread UI (#410).
 *
 * Conversations are derived from the activity event stream. Each event
 * carries a `source` address in one of two wire forms:
 *
 *   - Navigation form: `scheme://path`   (slug-based, used for humans and legacy)
 *   - Identity form:   `scheme:id:<uuid>` (UUID-based stable identity, used for
 *     agents and units after #1490)
 *
 * The UI maps the scheme to a small fixed set of presentation roles so
 * visually distinct bubbles stay consistent across the portal.
 */

export type ConversationRole = "human" | "agent" | "unit" | "tool" | "system";

export type AddressKind = "navigation" | "identity";

export interface ParsedThreadSource {
  scheme: string;
  path: string;
  /** The address kind: "navigation" for `scheme://path`, "identity" for `scheme:id:<uuid>`. */
  kind: AddressKind;
  /** Original raw source string. */
  raw: string;
}

/**
 * Splits a source address into its components. Accepts both wire forms:
 *
 *   - Navigation form `scheme://path` (humans, legacy agents)
 *   - Identity form `scheme:id:<uuid>` (agents and units post-#1490)
 *
 * Falls back to a `system://<raw>` navigation shape when the value
 * doesn't contain a recognised separator — the projection layer can emit
 * shorthand on platform-internal events.
 */
export function parseThreadSource(source: string): ParsedThreadSource {
  // Try identity form first: "scheme:id:<uuid>"
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

  // Fallback: no recognisable separator
  return { scheme: "system", path: source, kind: "navigation", raw: source };
}

/**
 * Returns true when the address belongs to the human scheme using the
 * navigation form (`human://`). Humans do not yet use the identity form
 * (`human:id:<uuid>`) — that lands in #1491.
 */
export function isHumanAddress(address: string): boolean {
  return address.startsWith("human://");
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
