// Shared URL helpers for the Explorer's client-only history management.
//
// The Explorer writes node/tab/subtab state via window.history.replaceState
// (not router.replace) to avoid triggering App Router RSC navigations on
// every click — see the comment in app/units/page.tsx. Components that read
// URL state use useSyncExternalStore with the helpers below so they stay in
// sync across writes from any Explorer surface.
//
// Two snapshots are exposed:
//   - getExplorerSearchSnapshot — returns `window.location.search`. Consumers
//     that only care about query state (sub-tab strips, tab state) pass this
//     straight to URLSearchParams.
//   - getExplorerPathnameSnapshot — returns `window.location.pathname`. The
//     `/explorer/units/<id>` route uses it to derive the selected node id
//     from the URL after raw history.replaceState writes (useParams won't
//     update on those).

export const EXPLORER_URL_CHANGE_EVENT = "spring-voyage:explorer-url-change";

export function subscribeExplorerUrl(onStoreChange: () => void): () => void {
  window.addEventListener("popstate", onStoreChange);
  window.addEventListener(EXPLORER_URL_CHANGE_EVENT, onStoreChange);
  return () => {
    window.removeEventListener("popstate", onStoreChange);
    window.removeEventListener(EXPLORER_URL_CHANGE_EVENT, onStoreChange);
  };
}

export function getExplorerSearchSnapshot(): string {
  return window.location.search;
}

export function getServerExplorerSearchSnapshot(): string {
  return "";
}

export function getExplorerPathnameSnapshot(): string {
  return window.location.pathname;
}

export function getServerExplorerPathnameSnapshot(): string {
  return "";
}

export function dispatchExplorerUrlChange(): void {
  window.dispatchEvent(new Event(EXPLORER_URL_CHANGE_EVENT));
}

/**
 * Strip dashes from a GUID so it can sit in a URL path segment (#2473).
 * Non-GUID identifiers (test fixtures, human-friendly names like
 * `engineering` or `unit-alpha-id`) pass through unchanged so we don't
 * silently change their meaning by collapsing the dashes.
 */
export function toExplorerPathSegment(id: string): string {
  const stripped = id.replace(/-/g, "");
  if (/^[0-9a-f]{32}$/i.test(stripped)) return stripped;
  return id;
}
