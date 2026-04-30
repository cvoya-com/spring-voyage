/**
 * Recently-used image reference history (#622 / #968).
 *
 * Persists up to `MAX_IMAGE_HISTORY` distinct image reference strings in
 * `localStorage` so the unit-creation wizard and agent-execution surfaces
 * can offer autocomplete suggestions without a backend round-trip.
 *
 * Design choices:
 *   * localStorage (not sessionStorage) — image references are useful across
 *     sessions. They contain no secrets, just public container image tags.
 *   * FIFO eviction with dedup on insert: a reference already in the list
 *     moves to the front rather than accumulating duplicates.
 *   * Quota / SecurityError failures are swallowed — loss of history is
 *     graceful degradation; the operator can still type freely.
 *   * SSR-safe: every call guards `typeof window`. The module is imported
 *     by `"use client"` components that may be server-rendered; the
 *     guards prevent `ReferenceError: localStorage is not defined`.
 */

const STORAGE_KEY = "spring.image-history.v1";
export const MAX_IMAGE_HISTORY = 20;

/**
 * Built-in agent-image references that ship with the platform.
 *
 * These are the images `deployment/build-agent-images.sh` builds locally
 * (and that the release workflow publishes to ghcr.io). The wizard surfaces
 * them as suggestions even on first use — before the user has ever submitted
 * an image — so the picker is never empty out of the box.
 *
 * Keep this list in sync with `deployment/build-agent-images.sh`. If a new
 * agent runtime image is added there, add it here too. The proper long-term
 * solution is a Web API endpoint + CLI command for listing available agent
 * runtime images (#1433); this hardcoded seed is the v0.1 expedient.
 */
export const BUILTIN_AGENT_IMAGES: readonly string[] = [
  "localhost/spring-voyage-agent-claude-code:latest",
  "localhost/spring-voyage-agent-dapr:latest",
  "ghcr.io/cvoya-com/agent-base:latest",
];

function readStorage(): string[] {
  if (typeof window === "undefined") return [];
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (v): v is string => typeof v === "string" && v.trim().length > 0,
    );
  } catch {
    return [];
  }
}

/**
 * Read the suggestion list the wizard renders into its `<datalist>`.
 *
 * Order: user-entered history first (most-recent first), then any built-in
 * agent image the user hasn't already explicitly recorded. This keeps the
 * list useful even on first run (built-ins are always there) without
 * pushing built-ins above an image the operator actually used.
 */
export function loadImageHistory(): string[] {
  const merged: string[] = [];
  const seen = new Set<string>();
  for (const ref of readStorage()) {
    if (!seen.has(ref)) {
      merged.push(ref);
      seen.add(ref);
    }
  }
  for (const ref of BUILTIN_AGENT_IMAGES) {
    if (!seen.has(ref)) {
      merged.push(ref);
      seen.add(ref);
    }
  }
  return merged;
}

/**
 * Add `reference` to the front of the persisted history list, deduplicating
 * and capping at `MAX_IMAGE_HISTORY`. Silently ignores blank strings, the
 * built-in seeds (no need to "remember" something that's always offered),
 * and storage errors.
 */
export function recordImageReference(reference: string): void {
  if (typeof window === "undefined") return;
  const trimmed = reference.trim();
  if (!trimmed) return;
  if (BUILTIN_AGENT_IMAGES.includes(trimmed)) return;
  try {
    const existing = readStorage();
    const deduped = existing.filter((r) => r !== trimmed);
    const next = [trimmed, ...deduped].slice(0, MAX_IMAGE_HISTORY);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  } catch {
    // Quota exceeded or SecurityError — best-effort.
  }
}
