import { render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { KeyRound } from "lucide-react";

import SettingsPage from "./page";
import { ExtensionProvider } from "@/lib/extensions/context";
import type { MergedExtensions } from "@/lib/extensions/registry";
import { defaultAuthContext } from "@/lib/extensions/defaults";
import type { DrawerPanel } from "@/lib/extensions/types";

// next/link renders an <a> so the test can assert hrefs directly.
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

function makePanels(): DrawerPanel[] {
  // Standalone panel bodies keep the test decoupled from the real panel
  // components (which hit React Query and `fetch`). The hub only needs
  // to prove that every panel's component renders inline inside a card
  // — the panel bodies themselves have their own specs.
  return [
    {
      id: "budget",
      label: "Tenant budget",
      icon: KeyRound,
      description: "Daily cost ceiling.",
      orderHint: 10,
      component: <p data-testid="panel-body-budget">Budget body</p>,
    },
    {
      id: "tenant-defaults",
      label: "Tenant defaults",
      icon: KeyRound,
      description: "LLM credentials inherited by every unit.",
      orderHint: 15,
      component: (
        <p data-testid="panel-body-tenant-defaults">Tenant defaults body</p>
      ),
    },
    {
      id: "auth",
      label: "Account",
      icon: KeyRound,
      description: "Current session and API tokens.",
      orderHint: 20,
      component: <p data-testid="panel-body-auth">Auth body</p>,
    },
    {
      id: "about",
      label: "About",
      icon: KeyRound,
      description: "Platform version and license reference.",
      orderHint: 90,
      component: <p data-testid="panel-body-about">About body</p>,
    },
  ];
}

function renderHub(panels: DrawerPanel[] = makePanels()) {
  const merged: MergedExtensions = {
    routes: [],
    actions: [],
    drawerPanels: panels,
    auth: defaultAuthContext,
    decorators: [],
    slots: {},
  };
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>
      <ExtensionProvider override={merged}>{children}</ExtensionProvider>
    </QueryClientProvider>
  );
  return render(<SettingsPage />, { wrapper: Wrapper });
}

describe("SettingsPage", () => {
  it("renders the h1 landmark", () => {
    renderHub();
    expect(
      screen.getByRole("heading", { level: 1, name: /settings/i }),
    ).toBeInTheDocument();
  });

  it("renders every drawer panel inline inside a card with its label + description", () => {
    renderHub();

    for (const panel of makePanels()) {
      const card = screen.getByTestId(`settings-panel-card-${panel.id}`);
      expect(card).toBeInTheDocument();
      // Header chrome — label visible inside the card.
      expect(card).toHaveTextContent(panel.label);
      if (panel.description) {
        expect(card).toHaveTextContent(panel.description);
      }
      // Body rendered inline (not behind a button / collapse).
      expect(
        screen.getByTestId(`panel-body-${panel.id}`),
      ).toBeInTheDocument();
    }
  });

  it("renders a tile link to each Settings subpage with the right href", () => {
    renderHub();

    const tilesGrid = screen.getByTestId("settings-tiles-grid");
    const links = Array.from(tilesGrid.querySelectorAll("a[href]")).map(
      (el) => ({
        href: el.getAttribute("href"),
        text: el.textContent ?? "",
      }),
    );

    const hrefs = links.map((l) => l.href);
    expect(hrefs).toEqual([
      "/settings/skills",
      "/settings/packages",
      "/settings/agent-runtimes",
      "/settings/system-configuration",
    ]);

    // The tile's text content carries both label and description; only
    // assert the label shows up — descriptions are free to evolve.
    const labelsByHref: Record<string, string> = {
      "/settings/skills": "Skills",
      "/settings/packages": "Packages",
      "/settings/agent-runtimes": "Agent runtimes",
      "/settings/system-configuration": "System configuration",
    };
    for (const link of links) {
      expect(link.text).toContain(labelsByHref[link.href ?? ""]);
    }
  });

  it("honours the registry order (hosted extensions slot in via orderHint)", () => {
    const panels = [
      ...makePanels(),
      {
        id: "tenants",
        label: "Tenants",
        icon: KeyRound,
        orderHint: 100,
        component: <p data-testid="panel-body-tenants">Tenants body</p>,
      } satisfies DrawerPanel,
    ];
    renderHub(panels);

    const cards = screen
      .getAllByTestId(/^settings-panel-card-/)
      .map((el) => el.getAttribute("data-testid"));
    expect(cards).toEqual([
      "settings-panel-card-budget",
      "settings-panel-card-tenant-defaults",
      "settings-panel-card-auth",
      "settings-panel-card-about",
      "settings-panel-card-tenants",
    ]);
  });

  it("hides the tenant section when the registry reports no panels", () => {
    renderHub([]);
    expect(screen.queryByTestId("settings-panels-grid")).toBeNull();
    // Subpage tiles still render — the hub is useful even with zero panels.
    expect(screen.getByTestId("settings-tiles-grid")).toBeInTheDocument();
  });
});
