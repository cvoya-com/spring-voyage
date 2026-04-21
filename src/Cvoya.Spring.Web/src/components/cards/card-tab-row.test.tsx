import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { CardTabRow, TabChip } from "./card-tab-row";

describe("TabChip", () => {
  it("dispatches onOpenTab with the chip's id and tab name when clicked", () => {
    const onOpenTab = vi.fn();
    render(
      <TabChip tab="Activity" id="engineering" onOpenTab={onOpenTab} />,
    );
    fireEvent.click(screen.getByTestId("card-tab-chip-activity"));
    expect(onOpenTab).toHaveBeenCalledTimes(1);
    expect(onOpenTab).toHaveBeenCalledWith("engineering", "Activity");
  });

  it("exposes a default accessible label and matching tooltip", () => {
    render(
      <TabChip tab="Messages" id="ada" onOpenTab={vi.fn()} />,
    );
    const btn = screen.getByTestId("card-tab-chip-messages");
    expect(btn).toHaveAttribute("aria-label", "Open Messages tab");
    expect(btn).toHaveAttribute("title", "Open Messages tab");
  });

  it("honours a custom accessible label override", () => {
    render(
      <TabChip
        tab="Activity"
        id="engineering"
        onOpenTab={vi.fn()}
        label="Open Activity tab for Engineering"
      />,
    );
    const btn = screen.getByTestId("card-tab-chip-activity");
    expect(btn).toHaveAttribute(
      "aria-label",
      "Open Activity tab for Engineering",
    );
  });

  it("stops click propagation so a click-to-open card parent is not triggered", () => {
    const onCardClick = vi.fn();
    const onOpenTab = vi.fn();
    render(
      <div onClick={onCardClick} data-testid="card-shell">
        <TabChip tab="Activity" id="engineering" onOpenTab={onOpenTab} />
      </div>,
    );
    fireEvent.click(screen.getByTestId("card-tab-chip-activity"));
    expect(onOpenTab).toHaveBeenCalledTimes(1);
    // The bubbling click never reaches the card shell.
    expect(onCardClick).not.toHaveBeenCalled();
  });
});

describe("CardTabRow", () => {
  it("renders one chip per tab in the supplied order", () => {
    render(
      <CardTabRow
        id="engineering"
        tabs={["Agents", "Messages", "Activity"]}
        onOpenTab={vi.fn()}
      />,
    );
    const row = screen.getByTestId("card-tab-row");
    const chips = row.querySelectorAll("[data-tab]");
    expect(Array.from(chips).map((c) => c.getAttribute("data-tab"))).toEqual([
      "Agents",
      "Messages",
      "Activity",
    ]);
  });

  it("renders nothing when the tabs list is empty", () => {
    const { container } = render(
      <CardTabRow id="engineering" tabs={[]} onOpenTab={vi.fn()} />,
    );
    expect(container.firstChild).toBeNull();
  });

  it("forwards the row id to every chip's onOpenTab dispatch", () => {
    const onOpenTab = vi.fn();
    render(
      <CardTabRow
        id="ada"
        tabs={["Skills", "Traces"]}
        onOpenTab={onOpenTab}
      />,
    );
    fireEvent.click(screen.getByTestId("card-tab-chip-skills"));
    fireEvent.click(screen.getByTestId("card-tab-chip-traces"));
    expect(onOpenTab.mock.calls).toEqual([
      ["ada", "Skills"],
      ["ada", "Traces"],
    ]);
  });
});
