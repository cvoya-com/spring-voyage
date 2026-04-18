"use client";

import { cn } from "@/lib/utils";
import { createContext, useContext, useState, type ReactNode } from "react";

interface TabsContextValue {
  value: string;
  onValueChange: (value: string) => void;
}

const TabsContext = createContext<TabsContextValue>({ value: "", onValueChange: () => {} });

export function Tabs({
  defaultValue,
  children,
  className,
}: {
  defaultValue: string;
  children: ReactNode;
  className?: string;
}) {
  const [value, setValue] = useState(defaultValue);
  return (
    <TabsContext.Provider value={{ value, onValueChange: setValue }}>
      <div className={className}>{children}</div>
    </TabsContext.Provider>
  );
}

export function TabsList({ children, className }: { children: ReactNode; className?: string }) {
  // At narrow viewports the tab list can carry more triggers than fit in
  // the viewport (e.g. the unit-detail tab bar has 11 tabs). Wrap the
  // inner flex row in an overflow-x-auto container so the bar scrolls
  // horizontally instead of forcing the whole page to overflow. `w-full`
  // on the outer wrapper keeps the scrollable region bounded to the
  // card / page column; the inner `inline-flex` preserves the pill
  // chrome DESIGN.md § 7.7 describes.
  return (
    <div className="w-full overflow-x-auto">
      <div
        className={cn(
          "inline-flex h-9 items-center justify-start rounded-lg bg-muted p-1 text-muted-foreground",
          className,
        )}
      >
        {children}
      </div>
    </div>
  );
}

export function TabsTrigger({
  value,
  children,
  className,
}: {
  value: string;
  children: ReactNode;
  className?: string;
}) {
  const ctx = useContext(TabsContext);
  return (
    <button
      className={cn(
        "inline-flex items-center justify-center whitespace-nowrap rounded-md px-3 py-1 text-sm font-medium transition-all",
        ctx.value === value
          ? "bg-background text-foreground shadow-sm"
          : "hover:text-foreground",
        className
      )}
      onClick={() => ctx.onValueChange(value)}
    >
      {children}
    </button>
  );
}

export function TabsContent({
  value,
  children,
  className,
}: {
  value: string;
  children: ReactNode;
  className?: string;
}) {
  const ctx = useContext(TabsContext);
  if (ctx.value !== value) return null;
  return <div className={cn("mt-2", className)}>{children}</div>;
}
