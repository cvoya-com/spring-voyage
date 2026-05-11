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

/**
 * Regex that matches every wire-form participant address Spring has
 * emitted, in any of the four shapes ADR-0036 / pre-#1629 lets through:
 *
 *   - canonical post-#1629:  `scheme:<32-hex>`
 *   - navigation-form:       `scheme://<32-hex>`
 *   - explicit identity:     `scheme:id:<dashed-uuid>` or `scheme:id:<32-hex>`
 *   - dashed-uuid path:      `scheme://<dashed-uuid>` (legacy slug)
 *
 * The scheme must be one of the address schemes the platform's
 * directory resolver understands (`human` / `agent` / `unit` / `tenant`).
 * The leading boundary is asserted with a non-capturing lookbehind on a
 * non-word character so we don't replace inside larger tokens (URLs,
 * package names, etc).
 *
 * Used by {@link renderBodyWithResolvedAddresses} to fold raw addresses
 * inside message bodies down to display names — extending the
 * server-side ParticipantRef resolver (#1635 / #1645) to body text so a
 * weak LLM that mimics the prompt-format and emits e.g.
 * `human://<guid>: …` doesn't leak the platform's addressing scheme
 * into the chat UI (#2089).
 */
const ADDRESS_FORM_REGEX =
  /(?<![A-Za-z0-9_])(human|agent|unit|tenant)(:\/\/|:id:|:)([0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b/g;

/**
 * Folds every raw `scheme:<guid>` / `scheme://<guid>` / `scheme:id:<uuid>`
 * occurrence inside a body string down to its resolved display name
 * (#2089). The resolver is supplied by the caller — typically the thread
 * row, which has the event's `source` / `from` / `to` ParticipantRefs
 * plus the thread's participant list; consumers map the address (with
 * its scheme prefix) to a `displayName` lookup.
 *
 * Rules:
 *  - When the resolver returns a non-null string, that string replaces
 *    the address in the rendered output (no link, no chip — keep it
 *    conversational).
 *  - When the resolver returns `null`, render the deleted-sentinel
 *    `<unknown>` (matches the server-side `<deleted>` shape so the UI
 *    voice stays consistent without leaking the raw GUID).
 *  - Surrounding text and whitespace are preserved; the function is
 *    idempotent over already-rendered bodies because the replaced text
 *    no longer matches {@link ADDRESS_FORM_REGEX}.
 *
 * Returns the rendered string. Callers wrap it in a `<p>` (or whatever
 * paragraph element they use); we do not emit React nodes here so the
 * helper stays trivially testable.
 */
export function renderBodyWithResolvedAddresses(
  body: string,
  resolveDisplayName: (address: string) => string | null,
): string {
  if (!body) return body;
  return body.replace(ADDRESS_FORM_REGEX, (match) => {
    const resolved = resolveDisplayName(match);
    return resolved ?? "<unknown>";
  });
}

/**
 * Builds an address → display-name resolver from a list of
 * `ParticipantRef`-shaped values. The lookup is keyed on the
 * participant's stable Guid identity (lowercase, undashed) so the same
 * resolver matches every wire-form address Spring has emitted for the
 * same actor — `human://savas`, `human:11111111…`,
 * `human:id:11111111-1111-…`, `human://11111111-1111-…`.
 *
 * The lookup uses {@link idOf} as the key normaliser; entries without
 * an id (legacy plain-string participants) are skipped because there is
 * no stable primitive to match on. Callers needing to resolve those
 * shapes should match by address-string equality at the call site.
 *
 * Returns a function suitable for passing to
 * {@link renderBodyWithResolvedAddresses}: given a raw address string
 * found inside a message body, returns the participant's
 * `displayName` (or `null` when no match is found, so the caller can
 * fall back to its preferred sentinel).
 */
export function buildParticipantNameResolver(
  participants: ReadonlyArray<AddressLike>,
): (address: string) => string | null {
  // Normalise each known participant's id into the 32-char no-dash form
  // so the lookup matches any wire shape the body might carry.
  const byNormalisedId = new Map<string, string>();
  for (const p of participants) {
    const id = idOf(p);
    if (!id) continue;
    const display = participantDisplayName(p);
    if (!display) continue;
    byNormalisedId.set(normaliseGuid(id), display);
  }

  return (rawAddress: string): string | null => {
    const parsed = parseThreadSource(rawAddress);
    const normalised = normaliseGuid(parsed.path);
    if (!normalised) return null;
    return byNormalisedId.get(normalised) ?? null;
  };
}

/**
 * Strips the dashes and lowercases a GUID-shaped string so dashed and
 * undashed forms compare equal. Returns the empty string when the input
 * is not GUID-shaped (so callers using the result as a Map key get a
 * deterministic miss rather than a partial match).
 */
function normaliseGuid(value: string): string {
  if (!value) return "";
  const stripped = value.replace(/-/g, "").toLowerCase();
  return /^[0-9a-f]{32}$/.test(stripped) ? stripped : "";
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
