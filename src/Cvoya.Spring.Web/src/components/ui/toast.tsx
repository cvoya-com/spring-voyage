"use client";

import { cn } from "@/lib/utils";
import {
  createContext,
  useCallback,
  useContext,
  useRef,
  useState,
  type ReactNode,
} from "react";

interface Toast {
  id: number;
  title: string;
  description?: string;
  variant?: "default" | "destructive";
}

interface ToastContextValue {
  toast: (t: Omit<Toast, "id">) => void;
}

const ToastContext = createContext<ToastContextValue>({ toast: () => {} });

export function useToast() {
  return useContext(ToastContext);
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  // A ref — not a plain `let` — so the id counter survives across renders.
  // Previously this was `let nextId = 0`, which reset on every render and
  // meant two quickly-scheduled toasts could share an id. The `useCallback`
  // identity is kept stable by not listing the ref in deps (refs are
  // stable by construction), which also satisfies
  // `react-hooks/exhaustive-deps`.
  const nextIdRef = useRef(0);

  const toast = useCallback((t: Omit<Toast, "id">) => {
    const id = ++nextIdRef.current;
    setToasts((prev) => [...prev, { ...t, id }]);
    setTimeout(() => setToasts((prev) => prev.filter((x) => x.id !== id)), 4000);
  }, []);

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div className="fixed bottom-4 right-4 z-50 flex flex-col gap-2">
        {toasts.map((t) => (
          <div
            key={t.id}
            className={cn(
              "rounded-lg border px-4 py-3 shadow-lg text-sm max-w-sm animate-in slide-in-from-bottom-2",
              t.variant === "destructive"
                ? "border-destructive/50 bg-destructive/10 text-destructive"
                : "border-border bg-card text-card-foreground"
            )}
          >
            <p className="font-medium">{t.title}</p>
            {t.description && <p className="mt-1 text-xs text-muted-foreground">{t.description}</p>}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}
