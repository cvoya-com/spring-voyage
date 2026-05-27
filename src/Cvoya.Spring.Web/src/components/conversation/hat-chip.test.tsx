// Tests for <HatChip /> — the shared read-side per-row Hat indicator
// (ADR-0062 § 5, #2807 / #2826 / #2829). The chip is consumed by the
// inbox, engagement list, unit / agent messaging-tab, and the inbox
// toolbar filter chip; these tests pin the visual + behavioural
// contract once so every caller stays aligned.

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { HatChip } from "./hat-chip";

describe("HatChip", () => {
  it("renders 'As <label>' with a 'Received as <label>' title when label is supplied", () => {
    render(<HatChip label="savas" testId="hat-chip-1" />);
    const chip = screen.getByTestId("hat-chip-1");
    expect(chip).toHaveTextContent("As savas");
    expect(chip).toHaveAttribute("title", "Received as savas");
  });

  it("renders the server's disambiguated label verbatim (with role suffix)", () => {
    render(<HatChip label="Bob — designer" testId="hat-chip-role" />);
    const chip = screen.getByTestId("hat-chip-role");
    expect(chip).toHaveTextContent("As Bob — designer");
    expect(chip).toHaveAttribute("title", "Received as Bob — designer");
  });

  it("returns null when label is null", () => {
    const { container } = render(
      <HatChip label={null} testId="hat-chip-null" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("returns null when label is undefined", () => {
    const { container } = render(<HatChip testId="hat-chip-undef" />);
    expect(container).toBeEmptyDOMElement();
  });

  it("returns null when label is blank or whitespace", () => {
    const { container } = render(
      <HatChip label="   " testId="hat-chip-ws" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("trims whitespace around the label on both the text and the title", () => {
    render(<HatChip label="  ada  " testId="hat-chip-trim" />);
    const chip = screen.getByTestId("hat-chip-trim");
    expect(chip).toHaveTextContent("As ada");
    expect(chip).toHaveAttribute("title", "Received as ada");
  });
});
