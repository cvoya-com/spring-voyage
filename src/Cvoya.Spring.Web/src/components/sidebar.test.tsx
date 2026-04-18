import { render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Building2 } from "lucide-react";

import { Sidebar } from "./sidebar";
import { ExtensionProvider } from "@/lib/extensions";
import {
  __resetExtensionsForTesting,
} from "@/lib/extensions/registry";
import { registerExtension } from "@/lib/extensions";

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
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

describe("Sidebar", () => {
  beforeEach(() => {
    __resetExtensionsForTesting();
  });

  afterEach(() => {
    __resetExtensionsForTesting();
  });

  it("renders the OSS default routes with no extensions registered", () => {
    render(
      <ExtensionProvider>
        <Sidebar />
      </ExtensionProvider>,
    );

    // The mobile-drawer copy duplicates the visible sidebar, so multiple
    // matches are expected.
    expect(screen.getAllByText("Dashboard").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Units").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Activity").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Initiative").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Budgets").length).toBeGreaterThan(0);

    // No hosted-only entries leaked in.
    expect(screen.queryByText("Tenants")).toBeNull();
    expect(screen.queryByText("Billing")).toBeNull();
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

    render(
      <ExtensionProvider>
        <Sidebar />
      </ExtensionProvider>,
    );

    expect(screen.getAllByText("Tenants").length).toBeGreaterThan(0);

    // Settings section label appears only when there are settings entries.
    expect(
      screen.getAllByTestId("sidebar-section-settings").length,
    ).toBeGreaterThan(0);
  });

  it("renders a Settings trigger when onOpenSettings is provided", () => {
    const onOpenSettings = vi.fn();
    render(
      <ExtensionProvider>
        <Sidebar onOpenSettings={onOpenSettings} />
      </ExtensionProvider>,
    );

    const triggers = screen.getAllByTestId("sidebar-settings-trigger");
    expect(triggers.length).toBeGreaterThan(0);
    triggers[0].click();
    expect(onOpenSettings).toHaveBeenCalled();
  });

  it("does not render a Settings trigger when onOpenSettings is not provided", () => {
    render(
      <ExtensionProvider>
        <Sidebar />
      </ExtensionProvider>,
    );

    expect(screen.queryByTestId("sidebar-settings-trigger")).toBeNull();
  });

  it("respects the permission gate on a registered route", () => {
    registerExtension({
      id: "hosted-rbac",
      routes: [
        {
          path: "/members",
          label: "Members",
          icon: Building2,
          navSection: "primary",
          permission: "members.view",
        },
      ],
      auth: {
        getUser: () => ({ id: "alice", displayName: "Alice" }),
        hasPermission: (key) => key !== "members.view",
        getHeaders: () => ({}),
      },
    });

    render(
      <ExtensionProvider>
        <Sidebar />
      </ExtensionProvider>,
    );

    expect(screen.queryByText("Members")).toBeNull();
  });
});
