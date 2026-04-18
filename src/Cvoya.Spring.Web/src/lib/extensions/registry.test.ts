import { describe, it, expect, beforeEach } from "vitest";
import { Building2, Package2, UserPlus } from "lucide-react";
import { createElement } from "react";

import {
  computeMergedExtensions,
  registerExtension,
  __resetExtensionsForTesting,
} from "./registry";
import { defaultActions, defaultDrawerPanels, defaultRoutes } from "./defaults";
import { authHeadersDecorator, withDecorators } from "./api";
import type { IAuthContext, FetchFn } from "./types";

describe("extension registry", () => {
  beforeEach(() => {
    __resetExtensionsForTesting();
  });

  it("returns OSS defaults when no extension is registered", () => {
    const merged = computeMergedExtensions();
    expect(merged.routes).toEqual(defaultRoutes);
    expect(merged.actions).toEqual(defaultActions);
    expect(merged.decorators).toEqual([]);
    expect(merged.auth.getUser()).toEqual({
      id: "local",
      displayName: "local",
    });
    expect(merged.auth.hasPermission("anything.at.all")).toBe(true);
  });

  it("merges routes and sorts by orderHint", () => {
    registerExtension({
      id: "test",
      routes: [
        {
          path: "/tenants",
          label: "Tenants",
          icon: Building2,
          navSection: "settings",
          orderHint: 5,
        },
      ],
    });

    const merged = computeMergedExtensions();
    const paths = merged.routes.map((r) => r.path);
    expect(paths).toContain("/tenants");

    // orderHint 5 sorts before the default dashboard (orderHint 10).
    expect(merged.routes[0].path).toBe("/tenants");
  });

  it("merges palette actions across extensions", () => {
    registerExtension({
      id: "ext-a",
      actions: [
        {
          id: "tenant.invite",
          label: "Invite teammate",
          icon: UserPlus,
          section: "hosted",
          href: "/tenants/invite",
          orderHint: 100,
        },
      ],
    });

    const merged = computeMergedExtensions();
    const ids = merged.actions.map((a) => a.id);
    expect(ids).toContain("tenant.invite");
    expect(ids).toContain("unit.create");
  });

  it("replaces a prior registration when the same id is re-registered", () => {
    registerExtension({
      id: "duplicate",
      actions: [
        {
          id: "old",
          label: "Old action",
          href: "/",
        },
      ],
    });
    registerExtension({
      id: "duplicate",
      actions: [
        {
          id: "new",
          label: "New action",
          href: "/",
        },
      ],
    });

    const ids = computeMergedExtensions().actions.map((a) => a.id);
    expect(ids).toContain("new");
    expect(ids).not.toContain("old");
  });

  it("replaces the auth adapter when an extension supplies one", () => {
    const stubAuth: IAuthContext = {
      getUser: () => ({ id: "alice", displayName: "Alice" }),
      hasPermission: (key) => key === "units.read",
      getHeaders: () => ({ Authorization: "Bearer xyz" }),
    };

    registerExtension({ id: "hosted", auth: stubAuth });

    const merged = computeMergedExtensions();
    expect(merged.auth.getUser()?.id).toBe("alice");
    expect(merged.auth.hasPermission("units.read")).toBe(true);
    expect(merged.auth.hasPermission("tenants.manage")).toBe(false);
  });

  it("throws when two extensions fight over the auth adapter", () => {
    registerExtension({
      id: "hosted-a",
      auth: {
        getUser: () => null,
        hasPermission: () => false,
        getHeaders: () => ({}),
      },
    });
    expect(() =>
      registerExtension({
        id: "hosted-b",
        auth: {
          getUser: () => null,
          hasPermission: () => false,
          getHeaders: () => ({}),
        },
      }),
    ).toThrow(/already owns/);
  });

  it("ships Budget / Auth / About as the default drawer panels", () => {
    const merged = computeMergedExtensions();
    const ids = merged.drawerPanels.map((p) => p.id);
    expect(ids).toEqual(["budget", "auth", "about"]);
    expect(merged.drawerPanels).toEqual(defaultDrawerPanels);
  });

  it("appends extension drawer panels and sorts by orderHint", () => {
    registerExtension({
      id: "hosted-panels",
      drawerPanels: [
        {
          id: "tenants",
          label: "Tenants",
          icon: Building2,
          orderHint: 100,
          component: createElement("div"),
        },
      ],
    });

    const merged = computeMergedExtensions();
    const ids = merged.drawerPanels.map((p) => p.id);
    expect(ids).toEqual(["budget", "auth", "about", "tenants"]);
  });

  it("replaces a default drawer panel when an extension re-uses its id", () => {
    registerExtension({
      id: "hosted-override",
      drawerPanels: [
        {
          id: "about",
          label: "About (hosted)",
          icon: Package2,
          orderHint: 90,
          component: createElement("div"),
        },
      ],
    });

    const merged = computeMergedExtensions();
    const about = merged.drawerPanels.find((p) => p.id === "about");
    expect(about?.label).toBe("About (hosted)");
    // Total panel count stays at 3 — the override replaces, it doesn't
    // duplicate.
    expect(merged.drawerPanels.length).toBe(3);
  });

  it("collects decorators in registration order", async () => {
    const trace: string[] = [];
    registerExtension({
      id: "ext-1",
      decorators: [
        (inner) => async (input, init) => {
          trace.push("ext-1");
          return inner(input, init);
        },
      ],
    });
    registerExtension({
      id: "ext-2",
      decorators: [
        (inner) => async (input, init) => {
          trace.push("ext-2");
          return inner(input, init);
        },
      ],
    });

    const baseFetch: FetchFn = async () =>
      new Response(null, { status: 204 });
    const merged = computeMergedExtensions();
    const decorated = withDecorators(baseFetch, merged.decorators);
    await decorated("/x");

    // Outer decorator runs before the inner one — registration order.
    expect(trace).toEqual(["ext-1", "ext-2"]);
  });
});

describe("authHeadersDecorator", () => {
  it("passes through when the auth context returns no headers", async () => {
    const auth: IAuthContext = {
      getUser: () => ({ id: "local", displayName: "local" }),
      hasPermission: () => true,
      getHeaders: () => ({}),
    };

    let seenInit: RequestInit | undefined;
    const inner: FetchFn = async (_input, init) => {
      seenInit = init;
      return new Response(null, { status: 204 });
    };

    const decorated = authHeadersDecorator(auth)(inner);
    await decorated("/api/v1/units", { headers: { Accept: "application/json" } });

    const headers = new Headers(seenInit?.headers);
    expect(headers.get("Accept")).toBe("application/json");
    expect(headers.get("Authorization")).toBeNull();
  });

  it("attaches auth headers without discarding existing ones", async () => {
    const auth: IAuthContext = {
      getUser: () => ({ id: "alice", displayName: "Alice" }),
      hasPermission: () => true,
      getHeaders: () => ({
        Authorization: "Bearer abc",
        "X-Tenant": "acme",
      }),
    };

    let seenHeaders: Headers | undefined;
    const inner: FetchFn = async (_input, init) => {
      seenHeaders = new Headers(init?.headers);
      return new Response(null, { status: 204 });
    };

    const decorated = authHeadersDecorator(auth)(inner);
    await decorated("/api/v1/units", {
      headers: { Accept: "application/json" },
    });

    expect(seenHeaders?.get("Accept")).toBe("application/json");
    expect(seenHeaders?.get("Authorization")).toBe("Bearer abc");
    expect(seenHeaders?.get("X-Tenant")).toBe("acme");
  });
});
