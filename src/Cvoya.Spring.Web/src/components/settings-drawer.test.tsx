import { render, screen, fireEvent } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Info, Package2 } from "lucide-react";

import { SettingsDrawer } from "./settings-drawer";
import { ExtensionProvider } from "@/lib/extensions";
import {
  __resetExtensionsForTesting,
} from "@/lib/extensions/registry";
import { registerExtension } from "@/lib/extensions";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ToastProvider } from "@/components/ui/toast";

// next/navigation is imported transitively via the budget panel's
// mutation hook chain; stub the pieces that touch the router.
vi.mock("next/navigation", () => ({
  usePathname: () => "/",
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}));

// openapi-fetch hits `fetch`; return static-but-sane shapes so the
// default panels don't throw. Tests that care about specific payloads
// override this per test.
function stubFetch() {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url.includes("/api/v1/tenant/budget")) {
      return new Response(JSON.stringify({ dailyBudget: 50 }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    if (url.includes("/api/v1/platform/info")) {
      return new Response(
        JSON.stringify({
          version: "1.2.3",
          buildHash: "abc1234",
          license: "LicenseRef-BSL-1.1",
        }),
        {
          status: 200,
          headers: { "Content-Type": "application/json" },
        },
      );
    }
    if (url.includes("/api/v1/auth/me")) {
      return new Response(
        JSON.stringify({ userId: "local-dev-user", displayName: "Local Developer" }),
        {
          status: 200,
          headers: { "Content-Type": "application/json" },
        },
      );
    }
    if (url.includes("/api/v1/auth/tokens")) {
      return new Response(JSON.stringify([]), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    return new Response("{}", { status: 200 });
  });
}

function wrap(ui: React.ReactNode) {
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return (
    <QueryClientProvider client={qc}>
      <ExtensionProvider>
        <ToastProvider>{ui}</ToastProvider>
      </ExtensionProvider>
    </QueryClientProvider>
  );
}

describe("SettingsDrawer", () => {
  beforeEach(() => {
    __resetExtensionsForTesting();
    globalThis.fetch = stubFetch() as unknown as typeof fetch;
  });

  afterEach(() => {
    __resetExtensionsForTesting();
    vi.restoreAllMocks();
  });

  it("renders nothing when closed", () => {
    render(wrap(<SettingsDrawer open={false} onClose={() => {}} />));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("renders the three OSS default panels in order when open", () => {
    render(wrap(<SettingsDrawer open={true} onClose={() => {}} />));

    expect(screen.getByRole("dialog")).toBeInTheDocument();
    // Budget (orderHint 10) → Auth (20) → About (90).
    expect(screen.getByTestId("settings-panel-budget")).toBeInTheDocument();
    expect(screen.getByTestId("settings-panel-auth")).toBeInTheDocument();
    expect(screen.getByTestId("settings-panel-about")).toBeInTheDocument();

    const panels = screen
      .getAllByTestId(/^settings-panel-/)
      .map((el) => el.getAttribute("data-testid"));
    expect(panels).toEqual([
      "settings-panel-budget",
      "settings-panel-auth",
      "settings-panel-about",
    ]);
  });

  it("calls onClose when the user presses Escape", () => {
    const onClose = vi.fn();
    render(wrap(<SettingsDrawer open={true} onClose={onClose} />));
    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("calls onClose when the user clicks the close button", () => {
    const onClose = vi.fn();
    render(wrap(<SettingsDrawer open={true} onClose={onClose} />));
    fireEvent.click(screen.getByLabelText("Close settings"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("renders panels registered by an extension in orderHint order", () => {
    registerExtension({
      id: "hosted",
      drawerPanels: [
        {
          id: "tenants",
          label: "Tenants",
          icon: Package2,
          orderHint: 100,
          component: <p data-testid="tenants-body">Tenant content</p>,
        },
      ],
    });

    render(wrap(<SettingsDrawer open={true} onClose={() => {}} />));

    // Hosted panel sits after the OSS defaults (orderHint 100 > 90).
    const panels = screen
      .getAllByTestId(/^settings-panel-/)
      .map((el) => el.getAttribute("data-testid"));
    expect(panels).toEqual([
      "settings-panel-budget",
      "settings-panel-auth",
      "settings-panel-about",
      "settings-panel-tenants",
    ]);
  });

  it("lets an extension override a default panel by reusing its id", () => {
    registerExtension({
      id: "hosted-override",
      drawerPanels: [
        {
          id: "about",
          label: "About (hosted)",
          icon: Info,
          orderHint: 90,
          component: (
            <p data-testid="settings-about-override">Hosted build</p>
          ),
        },
      ],
    });

    render(wrap(<SettingsDrawer open={true} onClose={() => {}} />));

    expect(screen.getByTestId("settings-about-override")).toBeInTheDocument();
    expect(screen.getByText("About (hosted)")).toBeInTheDocument();
  });

  it("hides a permission-gated panel when the auth adapter rejects the key", () => {
    registerExtension({
      id: "hosted-gated",
      drawerPanels: [
        {
          id: "members",
          label: "Members",
          icon: Package2,
          permission: "members.view",
          orderHint: 50,
          component: <p>Members content</p>,
        },
      ],
      auth: {
        getUser: () => ({ id: "alice", displayName: "Alice" }),
        hasPermission: (key) => key !== "members.view",
        getHeaders: () => ({}),
      },
    });

    render(wrap(<SettingsDrawer open={true} onClose={() => {}} />));
    expect(screen.queryByText("Members")).toBeNull();
  });
});
