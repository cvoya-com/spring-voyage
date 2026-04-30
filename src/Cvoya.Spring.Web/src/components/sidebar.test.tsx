import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { Building2 } from "lucide-react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { NavItemBadge, Sidebar } from "./sidebar";
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

  it("Ctrl+\\ keyboard shortcut toggles the sidebar collapse state", () => {
    const { container } = renderSidebar();

    const desktopAside = container.querySelector(
      'aside.hidden.md\\:flex',
    ) as HTMLElement;
    // Start expanded.
    expect(desktopAside.dataset.collapsed).toBeUndefined();

    // Fire Ctrl+backslash on the window.
    fireEvent.keyDown(window, { key: "\\", ctrlKey: true });
    expect(desktopAside.dataset.collapsed).toBe("true");

    // Fire again — back to expanded.
    fireEvent.keyDown(window, { key: "\\", ctrlKey: true });
    expect(desktopAside.dataset.collapsed).toBeUndefined();
  });

  it("Ctrl+\\ shortcut is ignored when an editable element is focused", () => {
    const { container } = renderSidebar();

    const desktopAside = container.querySelector(
      'aside.hidden.md\\:flex',
    ) as HTMLElement;
    expect(desktopAside.dataset.collapsed).toBeUndefined();

    // Simulate key-down on an input element (focus target).
    const input = document.createElement("input");
    document.body.appendChild(input);
    try {
      fireEvent.keyDown(input, { key: "\\", ctrlKey: true });
      // State should be unchanged because the shortcut handler skips
      // events whose target is an editable element.
      expect(desktopAside.dataset.collapsed).toBeUndefined();
    } finally {
      document.body.removeChild(input);
    }
  });
});

describe("Sidebar collapsed-rail polish (V21-collapsed-rail-polish)", () => {
  beforeEach(() => {
    __resetExtensionsForTesting();
    vi.stubGlobal("localStorage", stubStorage);
    memoryStore.clear();
    memoryStore.set("spring-voyage-sidebar-collapsed", "1");
  });
  afterEach(() => {
    __resetExtensionsForTesting();
    vi.unstubAllGlobals();
  });

  // The collapsed rail is the only surface where the hover tooltip
  // fires, so every test in this block starts with the sidebar already
  // collapsed via the storage preference.

  it("renders a closed tooltip per nav link when collapsed; none when expanded", () => {
    renderSidebar();
    // Find the desktop Dashboard link and walk up to its Tooltip wrapper.
    const dashboards = screen.getAllByRole("link", { name: "Dashboard" });
    const desktopDashboard = dashboards[dashboards.length - 1];
    const anchor = desktopDashboard.closest(
      '[data-slot="tooltip-anchor"]',
    ) as HTMLElement;
    expect(anchor).not.toBeNull();
    const tooltip = within(anchor).getByTestId("tooltip");
    expect(tooltip).toHaveAttribute("data-state", "closed");
    expect(tooltip).toHaveTextContent("Dashboard");

    // Expand the rail — every tooltip disappears.
    fireEvent.click(screen.getAllByTestId("sidebar-collapse-toggle")[0]);
    expect(screen.queryAllByTestId("tooltip")).toHaveLength(0);
  });

  it("shows the tooltip on hover after the delay and dismisses on blur + Escape", () => {
    vi.useFakeTimers();
    try {
      renderSidebar();

      const dashboards = screen.getAllByRole("link", { name: "Dashboard" });
      const desktopDashboard = dashboards[dashboards.length - 1];
      const anchor = desktopDashboard.closest(
        '[data-slot="tooltip-anchor"]',
      ) as HTMLElement;
      const tooltip = within(anchor).getByTestId("tooltip");

      // Hover — still closed until the 200 ms delay elapses.
      fireEvent.mouseEnter(desktopDashboard);
      expect(tooltip).toHaveAttribute("data-state", "closed");
      act(() => {
        vi.advanceTimersByTime(200);
      });
      expect(tooltip).toHaveAttribute("data-state", "open");

      // Leaving the link dismisses it.
      fireEvent.mouseLeave(desktopDashboard);
      expect(tooltip).toHaveAttribute("data-state", "closed");

      // Focus shows immediately (no delay for keyboard users).
      fireEvent.focus(desktopDashboard);
      expect(tooltip).toHaveAttribute("data-state", "open");

      // Escape dismisses, and blur dismisses too.
      fireEvent.keyDown(desktopDashboard, { key: "Escape" });
      expect(tooltip).toHaveAttribute("data-state", "closed");

      fireEvent.focus(desktopDashboard);
      fireEvent.blur(desktopDashboard);
      expect(tooltip).toHaveAttribute("data-state", "closed");
    } finally {
      vi.useRealTimers();
    }
  });

  it("positions a badge inside the icon box with the badge slot hook", () => {
    render(
      <NavItemBadge
        spec={{ ariaLabel: "3 unread", count: 3, tone: "destructive" }}
        collapsed={true}
      />,
    );
    // Badge is addressable by `data-slot` — the CSS contract every
    // caller relies on. `role="status"` carries the accessible label.
    const badge = screen.getByRole("status", { name: "3 unread" });
    expect(badge).toHaveAttribute("data-slot", "badge");
    expect(badge).toHaveTextContent("3");
    // Anchor top-right of the icon box, not spilling past the rail.
    expect(badge.className).toContain("-top-1");
    expect(badge.className).toContain("-right-1");
    // Ring-2 of card colour keeps it legible against any hover state.
    expect(badge.className).toContain("ring-2");
    expect(badge.className).toContain("ring-card");
    // `collapsed` annotates the badge so CSS can diverge later if needed.
    expect(badge).toHaveAttribute("data-collapsed", "true");
  });

  it("caps numeric badge counts at 99+", () => {
    render(
      <NavItemBadge
        spec={{ ariaLabel: "lots", count: 150, tone: "warning" }}
        collapsed={false}
      />,
    );
    const badge = screen.getByRole("status", { name: "lots" });
    expect(badge).toHaveTextContent("99+");
    // When expanded the `data-collapsed` annotation is absent.
    expect(badge).not.toHaveAttribute("data-collapsed");
  });

  it("renders a dotless status badge when no count is provided", () => {
    render(
      <NavItemBadge
        spec={{ ariaLabel: "connector error", tone: "destructive" }}
        collapsed={true}
      />,
    );
    const dot = screen.getByRole("status", { name: "connector error" });
    expect(dot).toBeEmptyDOMElement();
    // Dot sizing — `h-2 w-2` — so the rail doesn't clip it.
    expect(dot.className).toMatch(/h-2\s+w-2/);
  });

  it("keyboard focus order on the collapsed rail: skip link → nav links → footer controls → collapse toggle", () => {
    const { container } = renderSidebar();

    const desktopAside = container.querySelector(
      'aside.hidden.md\\:flex',
    ) as HTMLElement;
    expect(desktopAside.dataset.collapsed).toBe("true");

    const focusables = Array.from(
      container.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled])',
      ),
    );
    // BrandMark is rendered but must not pull tab focus on its own
    // (it's decorative in the collapsed header). If it were a button
    // or link it'd show up in the nodelist above — assert none of the
    // focusables are the BrandMark host.
    for (const el of focusables) {
      expect(el.getAttribute("data-testid")).not.toBe("brand-mark");
      expect(el.getAttribute("tabindex")).not.toBe("-1");
    }

    // Skip-to-main link leads; collapse toggle is the final focusable
    // in the desktop aside.
    const skip = container.querySelector(
      '[data-testid="skip-to-main"]',
    ) as HTMLElement;
    expect(skip).toBe(focusables[0]);

    const desktopFocusables = Array.from(
      desktopAside.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled])',
      ),
    );
    const last = desktopFocusables[desktopFocusables.length - 1];
    expect(last).toBe(
      within(desktopAside).getByTestId("sidebar-collapse-toggle"),
    );

    // The collapse toggle's aria-expanded reflects the collapsed state
    // and its aria-label describes the action.
    expect(last).toHaveAttribute("aria-expanded", "false");
    expect(last).toHaveAttribute("aria-label", "Expand sidebar");
  });

  it("focused collapsed nav link shows its tooltip automatically (no pointer needed)", () => {
    renderSidebar();
    const dashboards = screen.getAllByRole("link", { name: "Dashboard" });
    const desktopDashboard = dashboards[dashboards.length - 1];
    const tooltip = within(
      desktopDashboard.closest('[data-slot="tooltip-anchor"]') as HTMLElement,
    ).getByTestId("tooltip");
    expect(tooltip).toHaveAttribute("data-state", "closed");

    act(() => {
      desktopDashboard.focus();
      fireEvent.focus(desktopDashboard);
    });
    expect(tooltip).toHaveAttribute("data-state", "open");
    expect(desktopDashboard.getAttribute("aria-describedby")).toBe(tooltip.id);
  });

  it("collapsed nav link keeps a visible focus ring inside the 56px rail", () => {
    renderSidebar();
    const dashboards = screen.getAllByRole("link", { name: "Dashboard" });
    const desktopDashboard = dashboards[dashboards.length - 1];
    // `focus-visible:ring-inset` prevents the 2 px ring from being
    // clipped by the sidebar's right border.
    expect(desktopDashboard.className).toContain("focus-visible:ring-inset");
    expect(desktopDashboard.className).toContain("focus-visible:ring-2");
  });
});
