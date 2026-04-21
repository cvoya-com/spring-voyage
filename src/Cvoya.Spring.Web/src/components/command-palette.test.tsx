import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Bot, Plus, UserPlus } from "lucide-react";

import { CommandPaletteProvider } from "./command-palette";
import {
  ExplorerSelectionProvider,
  useExplorerSelection,
} from "./units/explorer-selection-context";
import { ExtensionProvider } from "@/lib/extensions";
import { __resetExtensionsForTesting } from "@/lib/extensions/registry";
import { registerExtension } from "@/lib/extensions";

const pushMock = vi.fn();
let currentPathname = "/";

vi.mock("next/navigation", () => ({
  useRouter: () => ({
    push: pushMock,
    replace: () => {},
    back: () => {},
    forward: () => {},
    refresh: () => {},
    prefetch: () => {},
  }),
  usePathname: () => currentPathname,
}));

describe("CommandPalette", () => {
  beforeEach(() => {
    pushMock.mockReset();
    currentPathname = "/";
    __resetExtensionsForTesting();
  });

  afterEach(() => {
    __resetExtensionsForTesting();
  });

  function renderPalette() {
    return render(
      <ExtensionProvider>
        <ExplorerSelectionProvider>
          <CommandPaletteProvider>
            <div>host</div>
          </CommandPaletteProvider>
        </ExplorerSelectionProvider>
      </ExtensionProvider>,
    );
  }

  it("opens on Cmd-K and renders the search input", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    await waitFor(() => {
      expect(screen.getByTestId("command-palette-input")).toBeInTheDocument();
    });
  });

  it("opens on Ctrl-K (non-macOS)", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "k", ctrlKey: true });

    await waitFor(() => {
      expect(screen.getByTestId("command-palette-input")).toBeInTheDocument();
    });
  });

  it("opens on the '/' key when focus is outside an input", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "/" });

    await waitFor(() => {
      expect(screen.getByTestId("command-palette-input")).toBeInTheDocument();
    });
  });

  it("does not hijack '/' when an input is focused", () => {
    render(
      <ExtensionProvider>
        <CommandPaletteProvider>
          <input data-testid="host-input" />
        </CommandPaletteProvider>
      </ExtensionProvider>,
    );

    const input = screen.getByTestId("host-input");
    input.focus();
    fireEvent.keyDown(input, { key: "/", target: input });

    expect(screen.queryByTestId("command-palette-input")).toBeNull();
  });

  it("closes on Escape", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    await waitFor(() => {
      expect(screen.getByTestId("command-palette-input")).toBeInTheDocument();
    });

    fireEvent.keyDown(window, { key: "Escape" });

    await waitFor(() => {
      expect(screen.queryByTestId("command-palette-input")).toBeNull();
    });
  });

  it("filters routes by typed text", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    const input = await screen.findByTestId("command-palette-input");
    fireEvent.change(input, { target: { value: "budgets" } });

    await waitFor(() => {
      expect(
        screen.getByTestId("command-palette-item-route:/budgets"),
      ).toBeInTheDocument();
    });
    // Route with an unrelated label is filtered out.
    expect(
      screen.queryByTestId("command-palette-item-route:/activity"),
    ).toBeNull();
  });

  it("navigates when a route entry is selected", async () => {
    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    const item = await screen.findByTestId(
      "command-palette-item-route:/units",
    );
    fireEvent.click(item);

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/units");
    });
  });

  it("invokes the onSelect callback of an action entry", async () => {
    const onSelect = vi.fn();
    registerExtension({
      id: "test-actions",
      actions: [
        {
          id: "test.action",
          label: "Test Action Name",
          icon: Plus,
          section: "test",
          onSelect,
        },
      ],
    });

    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    const input = await screen.findByTestId("command-palette-input");
    fireEvent.change(input, { target: { value: "test action" } });

    const item = await screen.findByTestId(
      "command-palette-item-action:test.action",
    );
    fireEvent.click(item);

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledTimes(1);
    });
  });

  it("surfaces extension-registered route and action entries", async () => {
    registerExtension({
      id: "hosted-test",
      routes: [
        {
          path: "/tenants",
          label: "Tenants",
          icon: UserPlus,
          navSection: "settings",
        },
      ],
      actions: [
        {
          id: "tenant.invite",
          label: "Invite teammate",
          icon: UserPlus,
          section: "hosted",
          href: "/tenants/invite",
        },
      ],
    });

    renderPalette();
    fireEvent.keyDown(window, { key: "k", metaKey: true });

    await screen.findByTestId("command-palette-item-route:/tenants");
    expect(
      screen.getByTestId("command-palette-item-action:tenant.invite"),
    ).toBeInTheDocument();
  });

  it("does not render when no shortcut has been pressed (OSS default load)", () => {
    renderPalette();
    expect(screen.queryByTestId("command-palette-input")).toBeNull();
  });

  describe("EXP-cmdk-bridge — node-kind action teleport", () => {
    function ListenerHost() {
      const { registerListener } = useExplorerSelection();
      // Register a listener so `hasListener()` returns true, mirroring a
      // mounted `<UnitExplorer>`.
      registerListener(listenerMock);
      return null;
    }

    const listenerMock = vi.fn();

    beforeEach(() => {
      listenerMock.mockReset();
    });

    it("teleports into the mounted Explorer when on /units", async () => {
      currentPathname = "/units";
      registerExtension({
        id: "cmdk-test-node",
        actions: [
          {
            id: "node.ada",
            label: "Go to Ada",
            icon: Bot,
            section: "nodes",
            explorerNodeId: "ada",
          },
        ],
      });

      render(
        <ExtensionProvider>
          <ExplorerSelectionProvider>
            <ListenerHost />
            <CommandPaletteProvider>
              <div>host</div>
            </CommandPaletteProvider>
          </ExplorerSelectionProvider>
        </ExtensionProvider>,
      );
      fireEvent.keyDown(window, { key: "k", metaKey: true });

      const item = await screen.findByTestId(
        "command-palette-item-action:node.ada",
      );
      fireEvent.click(item);

      await waitFor(() => expect(listenerMock).toHaveBeenCalledWith("ada"));
      expect(pushMock).not.toHaveBeenCalled();
    });

    it("navigates to /units?node=… when the Explorer is not mounted", async () => {
      currentPathname = "/";
      registerExtension({
        id: "cmdk-test-node",
        actions: [
          {
            id: "node.ada",
            label: "Go to Ada",
            icon: Bot,
            section: "nodes",
            explorerNodeId: "ada",
          },
        ],
      });

      renderPalette();
      fireEvent.keyDown(window, { key: "k", metaKey: true });

      const item = await screen.findByTestId(
        "command-palette-item-action:node.ada",
      );
      fireEvent.click(item);

      await waitFor(() =>
        expect(pushMock).toHaveBeenCalledWith("/units?node=ada"),
      );
    });

    it("navigates to /units?node=… when on /units but no Explorer is mounted", async () => {
      currentPathname = "/units";
      registerExtension({
        id: "cmdk-test-node",
        actions: [
          {
            id: "node.ada",
            label: "Go to Ada",
            icon: Bot,
            section: "nodes",
            explorerNodeId: "ada",
          },
        ],
      });

      // No `<ListenerHost>` — hasListener() is false, so the palette
      // falls back to navigation even though the pathname matches.
      renderPalette();
      fireEvent.keyDown(window, { key: "k", metaKey: true });

      const item = await screen.findByTestId(
        "command-palette-item-action:node.ada",
      );
      fireEvent.click(item);

      await waitFor(() =>
        expect(pushMock).toHaveBeenCalledWith("/units?node=ada"),
      );
    });

    it("URL-encodes node ids with special characters", async () => {
      currentPathname = "/";
      registerExtension({
        id: "cmdk-test-node",
        actions: [
          {
            id: "node.special",
            label: "Special",
            icon: Bot,
            section: "nodes",
            explorerNodeId: "alpha/one",
          },
        ],
      });

      renderPalette();
      fireEvent.keyDown(window, { key: "k", metaKey: true });

      const item = await screen.findByTestId(
        "command-palette-item-action:node.special",
      );
      fireEvent.click(item);

      await waitFor(() =>
        expect(pushMock).toHaveBeenCalledWith("/units?node=alpha%2Fone"),
      );
    });
  });
});
