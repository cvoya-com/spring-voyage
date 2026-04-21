import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { Tooltip } from "./tooltip";

describe("Tooltip", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("renders the anchor without opening the tooltip initially", () => {
    render(
      <Tooltip label="Dashboard">
        <button>Go</button>
      </Tooltip>,
    );

    expect(screen.getByRole("button", { name: "Go" })).toBeInTheDocument();
    // The tooltip node is mounted for transition purposes, but is
    // aria-hidden until opened.
    const tooltip = screen.getByTestId("tooltip");
    expect(tooltip).toHaveAttribute("aria-hidden", "true");
    expect(tooltip).toHaveAttribute("data-state", "closed");
    // Anchor shouldn't be described by a hidden tooltip.
    expect(screen.getByRole("button")).not.toHaveAttribute("aria-describedby");
  });

  it("opens after the hover delay and wires aria-describedby", () => {
    render(
      <Tooltip label="Dashboard" delayMs={200}>
        <button>Go</button>
      </Tooltip>,
    );

    const anchor = screen.getByRole("button");
    fireEvent.mouseEnter(anchor);

    // Before the delay elapses the tooltip is still closed.
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "closed");
    expect(anchor).not.toHaveAttribute("aria-describedby");

    act(() => {
      vi.advanceTimersByTime(200);
    });

    const tooltip = screen.getByTestId("tooltip");
    expect(tooltip).toHaveAttribute("data-state", "open");
    expect(tooltip).toHaveAttribute("aria-hidden", "false");
    expect(anchor.getAttribute("aria-describedby")).toBe(tooltip.id);
  });

  it("opens immediately on focus (no delay for keyboard users)", () => {
    render(
      <Tooltip label="Dashboard">
        <button>Go</button>
      </Tooltip>,
    );

    fireEvent.focus(screen.getByRole("button"));

    // No timer advance needed.
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "open");
  });

  it("dismisses on mouse leave", () => {
    render(
      <Tooltip label="Dashboard" delayMs={0}>
        <button>Go</button>
      </Tooltip>,
    );

    const anchor = screen.getByRole("button");
    fireEvent.mouseEnter(anchor);
    act(() => {
      vi.runAllTimers();
    });
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "open");

    fireEvent.mouseLeave(anchor);
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "closed");
  });

  it("dismisses on blur", () => {
    render(
      <Tooltip label="Dashboard">
        <button>Go</button>
      </Tooltip>,
    );
    fireEvent.focus(screen.getByRole("button"));
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "open");

    fireEvent.blur(screen.getByRole("button"));
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "closed");
  });

  it("dismisses on Escape and stops the key from bubbling", () => {
    const onParentKeyDown = vi.fn();

    render(
      <div onKeyDown={onParentKeyDown}>
        <Tooltip label="Dashboard">
          <button>Go</button>
        </Tooltip>
      </div>,
    );

    fireEvent.focus(screen.getByRole("button"));
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "open");

    fireEvent.keyDown(screen.getByRole("button"), { key: "Escape" });
    expect(screen.getByTestId("tooltip")).toHaveAttribute("data-state", "closed");
    // Escape while the tooltip was open should not propagate — otherwise
    // a parent dialog / collapse-toggle could close too.
    expect(onParentKeyDown).not.toHaveBeenCalled();
  });

  it("preserves child handlers", () => {
    const onMouseEnter = vi.fn();
    const onFocus = vi.fn();

    render(
      <Tooltip label="Dashboard">
        <button onMouseEnter={onMouseEnter} onFocus={onFocus}>
          Go
        </button>
      </Tooltip>,
    );

    const anchor = screen.getByRole("button");
    fireEvent.mouseEnter(anchor);
    fireEvent.focus(anchor);

    expect(onMouseEnter).toHaveBeenCalledTimes(1);
    expect(onFocus).toHaveBeenCalledTimes(1);
  });

  it("is completely inert when disabled", () => {
    render(
      <Tooltip label="Dashboard" enabled={false}>
        <button>Go</button>
      </Tooltip>,
    );

    expect(screen.queryByTestId("tooltip")).toBeNull();
    // No aria-describedby either — the anchor is the only rendered node.
    expect(screen.getByRole("button")).not.toHaveAttribute(
      "aria-describedby",
    );
  });
});
