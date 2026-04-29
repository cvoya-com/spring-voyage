/**
 * Role attribution helpers for the conversation thread UI (#410).
 *
 * Conversations are derived from the activity event stream. Each event
 * carries a `source` address shaped as `scheme://path` (the platform
 * persists the message envelope as activity events; see
 * `docs/architecture/messaging.md`). The UI maps the scheme to a small
 * fixed set of presentation roles so visually distinct bubbles stay
 * consistent across the portal.
 */

export type ConversationRole = "human" | "agent" | "unit" | "tool" | "system";

export interface ParsedThreadSource {
  scheme: string;
  path: string;
  /** Original `scheme://path` source string. */
  raw: string;
}

/**
 * Splits a `scheme://path` source into its components. Falls back to a
 * `system://<raw>` shape when the value doesn't contain a `://`
 * separator — the projection layer can emit shorthand on
 * platform-internal events.
 */
export function parseThreadSource(
  source: string,
): ParsedThreadSource {
  const idx = source.indexOf("://");
  if (idx <= 0) {
    return { scheme: "system", path: source, raw: source };
  }
  return {
    scheme: source.slice(0, idx).toLowerCase(),
    path: source.slice(idx + 3),
    raw: source,
  };
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
