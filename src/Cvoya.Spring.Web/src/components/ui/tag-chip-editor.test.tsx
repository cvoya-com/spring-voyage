import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

import { TagChipEditor } from "./tag-chip-editor";

// Controlled-wrapper helper so the tests can observe `onChange` while
// the component re-renders with the new value array (same pattern the
// consuming dialog uses).
function Harness({
  initial,
  variant,
  caseSensitive,
  testId = "tags",
}: {
  initial: string[];
  variant?: "row" | "stack";
  caseSensitive?: boolean;
  testId?: string;
}) {
  const [values, setValues] = useState<string[]>(initial);
  return (
    <TagChipEditor
      values={values}
      onChange={setValues}
      variant={variant}
      caseSensitive={caseSensitive}
      placeholder="Add a tag"
      testId={testId}
      aria-label="Tags"
    />
  );
}

describe("TagChipEditor (ADR-0045 Phase 4)", () => {
  it("renders one chip per value with an inline remove button", () => {
    render(<Harness initial={["alpha", "beta"]} />);

    expect(screen.getByText("alpha")).toBeInTheDocument();
    expect(screen.getByText("beta")).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Remove alpha" }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Remove beta" }),
    ).toBeInTheDocument();
  });

  it("appends a new value when the operator clicks Add", () => {
    render(<Harness initial={["alpha"]} />);

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "  beta  " },
    });
    fireEvent.click(screen.getByTestId("tags-add"));

    // The trimmed value lands in the chip row.
    expect(screen.getByText("beta")).toBeInTheDocument();
    // The textbox resets after a successful add.
    expect((screen.getByTestId("tags-input") as HTMLInputElement).value).toBe(
      "",
    );
  });

  it("appends on Enter without submitting a surrounding form", () => {
    const submit = vi.fn();
    render(
      <form onSubmit={submit}>
        <Harness initial={[]} />
      </form>,
    );

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "gamma" },
    });
    fireEvent.keyDown(screen.getByTestId("tags-input"), { key: "Enter" });

    expect(screen.getByText("gamma")).toBeInTheDocument();
    expect(submit).not.toHaveBeenCalled();
  });

  it("removes a value when its × button is clicked", () => {
    render(<Harness initial={["alpha", "beta"]} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove alpha" }));

    expect(screen.queryByText("alpha")).toBeNull();
    expect(screen.getByText("beta")).toBeInTheDocument();
  });

  it("rejects an exact duplicate and surfaces the inline hint", () => {
    render(<Harness initial={["alpha"]} />);

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "alpha" },
    });

    expect(screen.getByTestId("tags-duplicate-hint")).toBeInTheDocument();
    expect(screen.getByTestId("tags-add")).toBeDisabled();
    fireEvent.click(screen.getByTestId("tags-add"));
    // No second chip was added.
    expect(screen.getAllByText("alpha")).toHaveLength(1);
  });

  it("dedups case-insensitively by default", () => {
    render(<Harness initial={["Frontend"]} />);

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "frontend" },
    });
    fireEvent.click(screen.getByTestId("tags-add"));

    // The chip is still the original, single value.
    expect(screen.getAllByText(/frontend/i)).toHaveLength(1);
  });

  it("treats case-sensitive comparison as opt-in", () => {
    render(<Harness initial={["Frontend"]} caseSensitive />);

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "frontend" },
    });
    fireEvent.click(screen.getByTestId("tags-add"));

    expect(screen.getByText("Frontend")).toBeInTheDocument();
    expect(screen.getByText("frontend")).toBeInTheDocument();
  });

  it("rejects whitespace-only input", () => {
    render(<Harness initial={[]} />);

    fireEvent.change(screen.getByTestId("tags-input"), {
      target: { value: "   " },
    });

    expect(screen.getByTestId("tags-add")).toBeDisabled();
  });

  it("applies the stack layout class when variant=stack", () => {
    render(<Harness initial={["a", "b"]} variant="stack" />);

    const chips = screen.getByTestId("tags-chips");
    expect(chips.className).toContain("flex-col");
  });

  it("applies the row layout class when variant=row (default)", () => {
    render(<Harness initial={["a", "b"]} />);

    const chips = screen.getByTestId("tags-chips");
    expect(chips.className).toContain("flex-wrap");
  });

  it("disables every control when disabled", () => {
    render(
      <TagChipEditor
        values={["alpha"]}
        onChange={() => {}}
        disabled
        testId="tags"
      />,
    );

    expect(screen.getByTestId("tags-input")).toBeDisabled();
    expect(screen.getByTestId("tags-add")).toBeDisabled();
    expect(screen.getByRole("button", { name: "Remove alpha" })).toBeDisabled();
  });
});
