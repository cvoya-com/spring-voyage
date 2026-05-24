import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";

import {
  SystemPromptModeControl,
  type SystemPromptMode,
  type SystemPromptModeOrigin,
} from "./system-prompt-mode-control";

function renderControl(
  overrides: Partial<{
    effective: SystemPromptMode;
    origin: SystemPromptModeOrigin;
    onChange: (next: SystemPromptMode) => void;
    onClear?: () => void;
    busy: boolean;
    surface: "agent" | "unit";
  }> = {},
) {
  const onChange = overrides.onChange ?? vi.fn();
  const onClear = overrides.onClear ?? vi.fn();
  const utils = render(
    <SystemPromptModeControl
      effective={overrides.effective ?? "append"}
      origin={overrides.origin ?? "default"}
      onChange={onChange}
      onClear={onClear}
      busy={overrides.busy ?? false}
      surface={overrides.surface ?? "agent"}
    />,
  );
  return { ...utils, onChange, onClear };
}

describe("SystemPromptModeControl", () => {
  it("marks the option matching `effective` with aria-checked", () => {
    renderControl({ effective: "replace", origin: "agent" });
    expect(
      screen.getByTestId("system-prompt-mode-option-replace"),
    ).toHaveAttribute("aria-checked", "true");
    expect(
      screen.getByTestId("system-prompt-mode-option-append"),
    ).toHaveAttribute("aria-checked", "false");
  });

  it("labels the cascade indicator 'Inherited from unit' on the agent surface", () => {
    renderControl({ effective: "append", origin: "unit", surface: "agent" });
    const indicator = screen.getByTestId(
      "system-prompt-mode-cascade-indicator",
    );
    expect(indicator).toHaveTextContent("Inherited from unit");
    expect(indicator).toHaveAttribute("data-origin", "unit");
  });

  it("labels the cascade indicator 'Set here' on the unit surface when the unit declared a value", () => {
    renderControl({ effective: "replace", origin: "unit", surface: "unit" });
    const indicator = screen.getByTestId(
      "system-prompt-mode-cascade-indicator",
    );
    expect(indicator).toHaveTextContent("Set here");
  });

  it("invokes onChange when the operator clicks the non-selected option", () => {
    const { onChange } = renderControl({
      effective: "append",
      origin: "default",
    });
    fireEvent.click(screen.getByTestId("system-prompt-mode-option-replace"));
    expect(onChange).toHaveBeenCalledWith("replace");
  });

  it("does not invoke onChange when the operator re-selects the active option", () => {
    const { onChange } = renderControl({
      effective: "append",
      origin: "agent",
    });
    fireEvent.click(screen.getByTestId("system-prompt-mode-option-append"));
    expect(onChange).not.toHaveBeenCalled();
  });

  it("only renders Clear override when origin is 'agent'", () => {
    const { rerender, onClear } = renderControl({
      effective: "replace",
      origin: "unit",
      surface: "agent",
    });
    expect(
      screen.queryByTestId("system-prompt-mode-clear"),
    ).not.toBeInTheDocument();
    rerender(
      <SystemPromptModeControl
        effective="replace"
        origin="agent"
        onChange={() => {}}
        onClear={onClear}
        surface="agent"
      />,
    );
    const clearBtn = screen.getByTestId("system-prompt-mode-clear");
    fireEvent.click(clearBtn);
    expect(onClear).toHaveBeenCalledTimes(1);
  });

  it("disables every interactive element while busy", () => {
    renderControl({
      effective: "replace",
      origin: "agent",
      busy: true,
    });
    expect(
      screen.getByTestId("system-prompt-mode-option-append"),
    ).toBeDisabled();
    expect(
      screen.getByTestId("system-prompt-mode-option-replace"),
    ).toBeDisabled();
    expect(screen.getByTestId("system-prompt-mode-clear")).toBeDisabled();
  });

  it("passes axe in the unit surface", async () => {
    const { container } = renderControl({
      effective: "replace",
      origin: "unit",
      surface: "unit",
    });
    await expectNoAxeViolations(container);
  });
});
