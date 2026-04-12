"use client";

import { cn } from "@/lib/utils";
import { useTheme } from "@/lib/theme";
import {
  Activity,
  LayoutDashboard,
  Menu,
  Moon,
  Network,
  Sun,
  Wallet,
  X,
  Zap,
} from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";

interface NavItem {
  href: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
}

const navItems: NavItem[] = [
  { href: "/", label: "Dashboard", icon: LayoutDashboard },
  { href: "/units", label: "Units", icon: Network },
  { href: "/activity", label: "Activity", icon: Activity },
  { href: "/initiative", label: "Initiative", icon: Zap },
  { href: "/budgets", label: "Budgets", icon: Wallet },
];

export function Sidebar() {
  const pathname = usePathname();
  const { theme, toggleTheme } = useTheme();
  const [mobileOpen, setMobileOpen] = useState(false);

  useEffect(() => {
    setMobileOpen(false);
  }, [pathname]);

  const sidebarContent = (
    <>
      <div className="flex items-center justify-between px-4 py-4">
        <span className="text-lg font-bold">Spring Voyage</span>
        <button
          onClick={() => setMobileOpen(false)}
          className="md:hidden rounded-md p-1 text-muted-foreground hover:text-foreground"
          aria-label="Close sidebar"
        >
          <X className="h-5 w-5" />
        </button>
      </div>

      <nav className="flex-1 space-y-1 px-2 py-2 overflow-y-auto">
        {navItems.map((item) => (
          <NavLink key={item.href} item={item} pathname={pathname} />
        ))}
      </nav>

      <div className="border-t border-border px-4 py-3">
        <div className="flex items-center justify-between">
          <span className="text-xs text-muted-foreground">Spring Voyage v2</span>
          <button
            onClick={toggleTheme}
            className="rounded-md p-1 text-muted-foreground hover:text-foreground"
            title={`Switch to ${theme === "dark" ? "light" : "dark"} mode`}
          >
            {theme === "dark" ? (
              <Sun className="h-3.5 w-3.5" />
            ) : (
              <Moon className="h-3.5 w-3.5" />
            )}
          </button>
        </div>
      </div>
    </>
  );

  return (
    <>
      <button
        onClick={() => setMobileOpen(true)}
        className="fixed top-3 left-3 z-40 rounded-md border border-border bg-card p-2 text-muted-foreground hover:text-foreground md:hidden"
        aria-label="Open sidebar"
      >
        <Menu className="h-5 w-5" />
      </button>

      {mobileOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/50 md:hidden"
          onClick={() => setMobileOpen(false)}
        />
      )}

      <aside
        className={cn(
          "fixed inset-y-0 left-0 z-50 flex w-56 flex-col border-r border-border bg-card transition-transform duration-200 md:hidden",
          mobileOpen ? "translate-x-0" : "-translate-x-full"
        )}
      >
        {sidebarContent}
      </aside>

      <aside className="hidden md:flex h-screen w-56 flex-col border-r border-border bg-card">
        {sidebarContent}
      </aside>
    </>
  );
}

function NavLink({ item, pathname }: { item: NavItem; pathname: string }) {
  const active =
    item.href === "/"
      ? pathname === "/"
      : pathname === item.href || pathname.startsWith(item.href + "/");

  return (
    <Link
      href={item.href}
      className={cn(
        "flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors",
        active
          ? "bg-primary/10 text-primary font-medium"
          : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"
      )}
    >
      <item.icon className="h-4 w-4" />
      {item.label}
    </Link>
  );
}
