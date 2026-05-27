// Tests for <HatChip /> — the shared read-side per-row Hat indicator
// (ADR-0062 § 5, #2807 / #2826). The chip is consumed by the inbox,
// engagement list, and unit / agent messaging-tab; these tests pin the
// visual + behavioural contract once so all three callers stay aligned.

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { HatChip } from "./hat-chip";

describe("HatChip", () => {
  it("renders 'As <name>' with a 'Received as <name>' title when displayName is supplied", () => {
    render(<HatChip displayName="savas" testId="hat-chip-1" />);
    const chip = screen.getByTestId("hat-chip-1");
    expect(chip).toHaveTextContent("As savas");
    expect(chip).toHaveAttribute("title", "Received as savas");
  });

  it("returns null when displayName is null", () => {
    const { container } = render(
      <HatChip displayName={null} testId="hat-chip-null" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("returns null when displayName is undefined", () => {
    const { container } = render(<HatChip testId="hat-chip-undef" />);
    expect(container).toBeEmptyDOMElement();
  });

  it("returns null when displayName is blank or whitespace", () => {
    const { container } = render(
      <HatChip displayName="   " testId="hat-chip-ws" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("trims whitespace around the display name on both the label and the title", () => {
    render(<HatChip displayName="  ada  " testId="hat-chip-trim" />);
    const chip = screen.getByTestId("hat-chip-trim");
    expect(chip).toHaveTextContent("As ada");
    expect(chip).toHaveAttribute("title", "Received as ada");
  });
});
