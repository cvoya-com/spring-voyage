// Shared URL helpers for the Explorer's client-only history management.
//
// The Explorer writes node/tab/subtab state via window.history.replaceState
// (not router.replace) to avoid triggering App Router RSC navigations on
// every click — see the comment in app/units/page.tsx. Components that read
// URL state use useSyncExternalStore with the helpers below so they stay in
// sync across writes from any Explorer surface.

export const EXPLORER_URL_CHANGE_EVENT = "spring-voyage:explorer-url-change";

export function subscribeExplorerUrl(onStoreChange: () => void): () => void {
  window.addEventListener("popstate", onStoreChange);
  window.addEventListener(EXPLORER_URL_CHANGE_EVENT, onStoreChange);
  return () => {
    window.removeEventListener("popstate", onStoreChange);
    window.removeEventListener(EXPLORER_URL_CHANGE_EVENT, onStoreChange);
  };
}

export function getExplorerUrlSnapshot(): string {
  // Return both pathname and search so consumers can derive the
  // selected node from the path segment (new /explorer/units/<id>
  // style) as well as legacy ?node= query params.
  return window.location.pathname + window.location.search;
}

export function getServerExplorerUrlSnapshot(): string {
  return "";
}

export function dispatchExplorerUrlChange(): void {
  window.dispatchEvent(new Event(EXPLORER_URL_CHANGE_EVENT));
}
