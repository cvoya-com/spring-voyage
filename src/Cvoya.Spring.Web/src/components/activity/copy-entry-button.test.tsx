import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { CopyEntryButton } from "./copy-entry-button";

function setupClipboard() {
  const writeText = vi.fn().mockResolvedValue(undefined);
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: { writeText },
  });
  return writeText;
}

describe("CopyEntryButton (#2562)", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("writes a JSON-stringified copy of the entry to the clipboard", async () => {
    const writeText = setupClipboard();
    const entry = { id: "e-1", summary: "Unit started", severity: "Info" };
    render(<CopyEntryButton entry={entry} testId="row-1-copy" />);

    fireEvent.click(screen.getByTestId("row-1-copy"));

    await waitFor(() =>
      expect(writeText).toHaveBeenCalledWith(JSON.stringify(entry, null, 2)),
    );
  });

  it("flips the aria-label to 'Entry copied' after a successful copy", async () => {
    setupClipboard();
    render(<CopyEntryButton entry={{ id: "e-1" }} testId="row-1-copy" />);
    const btn = screen.getByTestId("row-1-copy");
    expect(btn).toHaveAttribute("aria-label", "Copy entry");

    fireEvent.click(btn);
    await waitFor(() =>
      expect(btn).toHaveAttribute("aria-label", "Entry copied"),
    );
  });

  it("stops the click from propagating so a clickable row container doesn't toggle", async () => {
    const writeText = setupClipboard();
    const rowClick = vi.fn();
    render(
      <div onClick={rowClick}>
        <CopyEntryButton entry={{ id: "e-1" }} testId="row-1-copy" />
      </div>,
    );
    fireEvent.click(screen.getByTestId("row-1-copy"));
    // Awaiting the clipboard side-effect lets React flush the `setCopied`
    // state update inside `act()` — otherwise the post-test state flip
    // surfaces as the testing-library "not wrapped in act(...)" warning.
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    expect(rowClick).not.toHaveBeenCalled();
  });

  it("swallows clipboard failures so the row stays usable", async () => {
    const writeText = vi.fn().mockRejectedValue(new Error("denied"));
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: { writeText },
    });
    render(<CopyEntryButton entry={{ id: "e-1" }} testId="row-1-copy" />);
    const btn = screen.getByTestId("row-1-copy");
    fireEvent.click(btn);
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    expect(btn).toHaveAttribute("aria-label", "Copy entry");
  });
});
