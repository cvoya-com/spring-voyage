// Behavioural tests for the Cmd-K ⇄ Explorer bridge
// (EXP-cmdk-bridge, umbrella #815). The palette dispatches through
// the context; when an Explorer is mounted on `/units` the dispatch
// lands in-place. Otherwise it's a no-op and the caller navigates.

import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { describe, expect, it, vi } from "vitest";

import {
  ExplorerSelectionProvider,
  useExplorerSelection,
} from "./explorer-selection-context";

function wrap(children: ReactNode) {
  return <ExplorerSelectionProvider>{children}</ExplorerSelectionProvider>;
}

describe("ExplorerSelectionProvider", () => {
  it("reports no listener before any Explorer registers", () => {
    const { result } = renderHook(() => useExplorerSelection(), {
      wrapper: ({ children }) => wrap(children),
    });
    expect(result.current.hasListener()).toBe(false);
  });

  it("hasListener flips to true while a listener is registered", () => {
    const { result } = renderHook(() => useExplorerSelection(), {
      wrapper: ({ children }) => wrap(children),
    });

    let unregister: (() => void) | null = null;
    act(() => {
      unregister = result.current.registerListener(() => {});
    });
    expect(result.current.hasListener()).toBe(true);

    act(() => unregister?.());
    expect(result.current.hasListener()).toBe(false);
  });

  it("dispatchSelect forwards the node id to the registered listener", () => {
    const { result } = renderHook(() => useExplorerSelection(), {
      wrapper: ({ children }) => wrap(children),
    });
    const setSelected = vi.fn();

    act(() => {
      result.current.registerListener(setSelected);
    });
    act(() => {
      result.current.dispatchSelect("unit-eng");
    });

    expect(setSelected).toHaveBeenCalledWith("unit-eng");
  });

  it("dispatchSelect is a no-op after the listener unregisters", () => {
    const { result } = renderHook(() => useExplorerSelection(), {
      wrapper: ({ children }) => wrap(children),
    });
    const setSelected = vi.fn();

    act(() => {
      const unregister = result.current.registerListener(setSelected);
      unregister();
    });
    act(() => {
      result.current.dispatchSelect("unit-eng");
    });

    expect(setSelected).not.toHaveBeenCalled();
  });

  it("a re-register replaces the prior listener (HMR / route transitions)", () => {
    const { result } = renderHook(() => useExplorerSelection(), {
      wrapper: ({ children }) => wrap(children),
    });
    const first = vi.fn();
    const second = vi.fn();

    act(() => {
      result.current.registerListener(first);
      result.current.registerListener(second);
    });
    act(() => {
      result.current.dispatchSelect("unit-eng");
    });

    expect(first).not.toHaveBeenCalled();
    expect(second).toHaveBeenCalledWith("unit-eng");
  });

  it("returns a safe no-op context outside a provider (isolated component tests)", () => {
    const { result } = renderHook(() => useExplorerSelection());
    expect(result.current.hasListener()).toBe(false);
    // Must not throw.
    expect(() => result.current.dispatchSelect("anything")).not.toThrow();
    const unregister = result.current.registerListener(() => {});
    expect(() => unregister()).not.toThrow();
  });
});
