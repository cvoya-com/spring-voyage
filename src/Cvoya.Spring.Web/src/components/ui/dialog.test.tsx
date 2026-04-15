import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { Dialog } from "./dialog";

describe("Dialog", () => {
  afterEach(() => {
    // Ensure body scroll-lock is cleared between tests.
    document.body.style.overflow = "";
  });

  it("does not render when closed", () => {
    render(
      <Dialog open={false} onClose={vi.fn()} title="Hidden">
        <p>body</p>
      </Dialog>,
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("renders with accessible name and description when open", () => {
    render(
      <Dialog
        open={true}
        onClose={vi.fn()}
        title="Edit membership"
        description="Update config"
      >
        <p>body</p>
      </Dialog>,
    );

    const dialog = screen.getByRole("dialog");
    expect(dialog).toHaveAttribute("aria-modal", "true");
    expect(dialog).toHaveAccessibleName("Edit membership");
    expect(dialog).toHaveAccessibleDescription("Update config");
  });

  it("closes on ESC and on backdrop click, but not when clicking inside", () => {
    const onClose = vi.fn();
    render(
      <Dialog open={true} onClose={onClose} title="T">
        <button>inside</button>
      </Dialog>,
    );

    // ESC closes.
    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);

    onClose.mockReset();

    // Click inside the panel does not close.
    fireEvent.mouseDown(screen.getByRole("button", { name: /inside/i }));
    expect(onClose).not.toHaveBeenCalled();

    // Backdrop mousedown closes.
    fireEvent.mouseDown(screen.getByTestId("dialog-backdrop"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("locks body scroll while open and restores it after close", () => {
    const { rerender } = render(
      <Dialog open={true} onClose={vi.fn()} title="T">
        <p>body</p>
      </Dialog>,
    );
    expect(document.body.style.overflow).toBe("hidden");

    rerender(
      <Dialog open={false} onClose={vi.fn()} title="T">
        <p>body</p>
      </Dialog>,
    );
    expect(document.body.style.overflow).not.toBe("hidden");
  });
});
