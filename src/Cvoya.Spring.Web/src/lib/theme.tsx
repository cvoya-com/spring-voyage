"use client";

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useSyncExternalStore,
  type ReactNode,
} from "react";

type Theme = "dark" | "light";

interface ThemeContextValue {
  theme: Theme;
  toggleTheme: () => void;
}

const ThemeContext = createContext<ThemeContextValue>({
  theme: "dark",
  toggleTheme: () => {},
});

export function useTheme() {
  return useContext(ThemeContext);
}

const STORAGE_KEY = "spring-voyage-theme";

// External-store adapter around `localStorage`. Using
// `useSyncExternalStore` avoids the `react-hooks/set-state-in-effect`
// anti-pattern we'd otherwise hit by reading localStorage inside a
// `useEffect` and calling `setTheme`. The subscribe function listens for
// cross-tab `storage` events so a theme change in another tab is picked
// up immediately.
function subscribeToStorage(onChange: () => void): () => void {
  if (typeof window === "undefined") {
    return () => {};
  }
  window.addEventListener("storage", onChange);
  return () => window.removeEventListener("storage", onChange);
}

function readStoredTheme(): Theme {
  if (typeof window === "undefined") return "dark";
  const stored = window.localStorage.getItem(STORAGE_KEY);
  return stored === "light" || stored === "dark" ? stored : "dark";
}

// Server snapshot: always "dark" so SSR output is deterministic and
// matches the default `<html class="dark">` set in the root layout.
// Any client-side divergence is reconciled on hydration via the external
// store subscription.
function serverSnapshot(): Theme {
  return "dark";
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const theme = useSyncExternalStore(
    subscribeToStorage,
    readStoredTheme,
    serverSnapshot,
  );

  // Keep the DOM className in sync with the React-owned theme. This is a
  // pure "React → external system" synchronization with no setState call,
  // so it does not trigger `react-hooks/set-state-in-effect`.
  useEffect(() => {
    document.documentElement.className = theme;
  }, [theme]);

  const toggleTheme = useCallback(() => {
    const current = readStoredTheme();
    const next: Theme = current === "dark" ? "light" : "dark";
    window.localStorage.setItem(STORAGE_KEY, next);
    // `setItem` does not fire `storage` events in the originating tab, so
    // dispatch one manually to nudge `useSyncExternalStore` to re-read.
    // The DOM className is kept in sync by the effect above.
    window.dispatchEvent(new StorageEvent("storage", { key: STORAGE_KEY }));
  }, []);

  return (
    <ThemeContext.Provider value={{ theme, toggleTheme }}>
      {children}
    </ThemeContext.Provider>
  );
}
