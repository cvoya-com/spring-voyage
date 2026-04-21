import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { AppShell } from "./app-shell";
import { __resetExtensionsForTesting } from "@/lib/extensions/registry";

vi.mock("next/navigation", () => ({
  usePathname: () => "/",
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("next/image", () => ({
  default: ({
    src,
    alt,
    ...rest
  }: {
    src: string;
    alt: string;
  } & Record<string, unknown>) => (
    // eslint-disable-next-line @next/next/no-img-element -- test stub, jsdom doesn't run the Next.js image optimizer
    <img src={src} alt={alt} {...rest} />
  ),
}));

vi.mock("@/lib/api/queries", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/queries")>(
    "@/lib/api/queries",
  );
  return {
    ...actual,
    usePlatformInfo: () => ({
      data: { version: "2.0.0", buildHash: "abc", license: "BSL-1.1" },
      isLoading: false,
      isError: false,
    }),
  };
});

function renderShell() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <AppShell>
        <h1>Page body</h1>
      </AppShell>
    </QueryClientProvider>,
  );
}

describe("AppShell", () => {
  beforeEach(() => __resetExtensionsForTesting());
  afterEach(() => __resetExtensionsForTesting());

  it("renders the sidebar + children + command palette without mounting the legacy SettingsDrawer", () => {
    renderShell();

    // Sidebar chrome is present.
    expect(screen.getAllByTestId("sidebar-header").length).toBeGreaterThan(0);
    // Page body renders inside the main landmark.
    expect(screen.getByRole("main")).toHaveTextContent("Page body");

    // SettingsDrawer is no longer mounted by the shell (IA-appshell).
    expect(screen.queryByTestId("settings-drawer")).toBeNull();
    expect(screen.queryByTestId("settings-drawer-backdrop")).toBeNull();
    // ...and the sidebar no longer renders a drawer trigger either.
    expect(screen.queryByTestId("sidebar-settings-trigger")).toBeNull();
  });
});
