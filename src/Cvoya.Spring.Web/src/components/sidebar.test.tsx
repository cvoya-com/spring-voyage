import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen } from "@testing-library/react";
import { Building2 } from "lucide-react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Sidebar } from "./sidebar";
import { ExtensionProvider, registerExtension } from "@/lib/extensions";
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

// `next/image` renders a real `<img>` in tests so the DOM keeps
// matching axe expectations — stub with a plain img to avoid the
// Next image optimizer poking at our jsdom.
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

// The footer's version pill reads `usePlatformInfo`; stub the network
// so we can assert the pill without spinning up MSW.
vi.mock("@/lib/api/queries", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/queries")>(
    "@/lib/api/queries",
  );
  return {
    ...actual,
    usePlatformInfo: () => ({
      data: { version: "2.0.0", buildHash: "abc123", license: "BSL-1.1" },
      isLoading: false,
      isError: false,
    }),
  };
});

function renderSidebar() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <ExtensionProvider>
        <Sidebar />
      </ExtensionProvider>
    </QueryClientProvider>,
  );
}

// `jsdom` in this harness ships a skeletal Storage that's missing
// setItem/getItem/removeItem. Stub a real backing map so the sidebar's
// collapse-state persistence is actually exercised under test.
const memoryStore = new Map<string, string>();
const stubStorage: Storage = {
  get length() {
    return memoryStore.size;
  },
  clear: () => memoryStore.clear(),
  getItem: (k) => memoryStore.get(k) ?? null,
  key: (i) => Array.from(memoryStore.keys())[i] ?? null,
  removeItem: (k) => {
    memoryStore.delete(k);
  },
  setItem: (k, v) => {
    memoryStore.set(k, v);
  },
};

describe("Sidebar chrome (IA-sidebar-chrome)", () => {
  beforeEach(() => {
    __resetExtensionsForTesting();
    vi.stubGlobal("localStorage", stubStorage);
    memoryStore.clear();
  });
  afterEach(() => {
    __resetExtensionsForTesting();
    vi.unstubAllGlobals();
  });

  it("renders the BrandMark + wordmark + env pill header", () => {
    renderSidebar();

    expect(screen.getAllByTestId("sidebar-header").length).toBeGreaterThan(0);
    expect(screen.getAllByTestId("brand-mark").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Spring Voyage").length).toBeGreaterThan(0);
    expect(
      screen.getAllByTestId("sidebar-env-pill")[0],
    ).toHaveTextContent(/env · local-dev/i);
  });

  it("renders group headers for every visible IA cluster", () => {
    renderSidebar();
    // Three default clusters ship default entries; `settings` is empty
    // in OSS so no header until a hosted extension adds to it.
    expect(
      screen.getAllByTestId("sidebar-section-label-overview")[0],
    ).toHaveTextContent("Overview");
    expect(
      screen.getAllByTestId("sidebar-section-label-orchestrate")[0],
    ).toHaveTextContent("Orchestrate");
    expect(
      screen.getAllByTestId("sidebar-section-label-control")[0],
    ).toHaveTextContent("Control");
  });

  it("lists every v2 default route under its declared cluster", () => {
    renderSidebar();
    // Assert representatives per cluster — drawer + desktop duplicate
    // the nav, so we tolerate multiple matches.
    for (const label of [
      "Dashboard",
      "Activity",
      "Analytics",
      "Units",
      "Inbox",
      "Discovery",
      "Connectors",
      "Policies",
      "Budgets",
      "Settings",
    ]) {
      expect(screen.getAllByText(label).length).toBeGreaterThan(0);
    }
  });

  it("does not surface retired top-level routes", () => {
    renderSidebar();
    for (const gone of [
      "Agents",
      "Conversations",
      "Initiative",
      "Packages",
      "Directory",
      "System configuration",
    ]) {
      expect(screen.queryByText(gone)).toBeNull();
    }
  });

  it("renders settings-section entries supplied by an extension", () => {
    registerExtension({
      id: "hosted",
      routes: [
        {
          path: "/tenants",
          label: "Tenants",
          icon: Building2,
          navSection: "settings",
          orderHint: 10,
        },
      ],
    });
    renderSidebar();

    expect(screen.getAllByText("Tenants").length).toBeGreaterThan(0);
    expect(
      screen.getAllByTestId("sidebar-section-label-settings")[0],
    ).toHaveTextContent("Settings");
  });

  it("respects the permission gate on a registered route", () => {
    registerExtension({
      id: "hosted-rbac",
      routes: [
        {
          path: "/members",
          label: "Members",
          icon: Building2,
          navSection: "settings",
          permission: "members.view",
        },
      ],
      auth: {
        getUser: () => ({ id: "alice", displayName: "Alice" }),
        hasPermission: (key) => key !== "members.view",
        getHeaders: () => ({}),
      },
    });
    renderSidebar();
    expect(screen.queryByText("Members")).toBeNull();
  });

  it("renders the footer user block with initial, display name, and status dot", () => {
    registerExtension({
      id: "hosted-auth",
      auth: {
        getUser: () => ({
          id: "alice",
          displayName: "Alice Example",
          email: "alice@example.com",
        }),
        hasPermission: () => true,
        getHeaders: () => ({}),
      },
    });
    renderSidebar();

    const users = screen.getAllByTestId("sidebar-user");
    expect(users[0]).toHaveTextContent("Alice Example");
    expect(users[0]).toHaveTextContent("alice@example.com");
    expect(users[0]).toHaveTextContent("A"); // initial
    expect(screen.getAllByTestId("sidebar-user-status").length).toBeGreaterThan(
      0,
    );
  });

  it("renders the version pill from usePlatformInfo", () => {
    renderSidebar();
    expect(screen.getAllByTestId("sidebar-version")[0]).toHaveTextContent(
      "v2.0.0",
    );
  });

  it("toggles the collapsed state + persists to localStorage", () => {
    const { container } = renderSidebar();

    const desktopAside = container.querySelector(
      'aside.hidden.md\\:flex',
    ) as HTMLElement;
    expect(desktopAside.dataset.collapsed).toBeUndefined();

    const toggle = screen.getAllByTestId("sidebar-collapse-toggle")[0];
    fireEvent.click(toggle);

    expect(desktopAside.dataset.collapsed).toBe("true");
    expect(
      window.localStorage.getItem("spring-voyage-sidebar-collapsed"),
    ).toBe("1");
    // Group labels collapse away in the narrow rail.
    expect(
      screen.queryByTestId("sidebar-section-label-overview"),
    ).toBeNull();
    // Env pill + version + email all hide when collapsed.
    expect(screen.queryByTestId("sidebar-env-pill")).toBeNull();
    expect(screen.queryByTestId("sidebar-version")).toBeNull();
  });

  it("no longer surfaces the legacy Settings-drawer trigger", () => {
    renderSidebar();
    expect(screen.queryByTestId("sidebar-settings-trigger")).toBeNull();
  });
});
