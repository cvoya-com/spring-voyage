// Tests for `<StructuredPayloadCard>` (#2128).
//
// The card is the visual home for a JSON envelope peeled out of a
// `MessageArrived` body when the body starts with a parseable JSON
// object. Behaviour: starts collapsed, click expands, expanded state
// shows a pretty-printed JSON dump in a font-mono pre block.

import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { StructuredPayloadCard } from "./structured-payload-card";

describe("StructuredPayloadCard", () => {
  it("starts collapsed by default", () => {
    render(<StructuredPayloadCard payload={{ a: 1 }} />);

    const toggle = screen.getByRole("button", { name: "Structured payload" });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    // The pretty-printed body is not in the DOM until the user expands.
    expect(screen.queryByText(/"a": 1/)).toBeNull();
  });

  it("expands on click and renders the pretty-printed JSON", () => {
    render(
      <StructuredPayloadCard payload={{ data: { has_approved_review: true } }} />,
    );

    const toggle = screen.getByRole("button", { name: "Structured payload" });
    fireEvent.click(toggle);

    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    const body = screen.getByTestId("structured-payload-body");
    expect(body.textContent).toContain('"has_approved_review": true');
    // Two-space indented — the inner object appears on its own line.
    expect(body.textContent).toContain('"data": {');
  });

  it("collapses again on a second click", () => {
    render(<StructuredPayloadCard payload={{ a: 1 }} />);
    const toggle = screen.getByRole("button", { name: "Structured payload" });

    fireEvent.click(toggle);
    expect(toggle.getAttribute("aria-expanded")).toBe("true");
    fireEvent.click(toggle);
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByTestId("structured-payload-body")).toBeNull();
  });

  it("respects the defaultExpanded prop", () => {
    render(<StructuredPayloadCard payload={{ a: 1 }} defaultExpanded />);
    expect(screen.getByTestId("structured-payload-body")).toBeTruthy();
  });

  it("uses a custom label when provided", () => {
    render(
      <StructuredPayloadCard payload={{ a: 1 }} label="Tool result" />,
    );
    expect(screen.getByRole("button", { name: "Tool result" })).toBeTruthy();
    expect(screen.getByLabelText("Tool result")).toBeTruthy();
  });

  it("exposes an accessible name on the section root", () => {
    render(<StructuredPayloadCard payload={{ a: 1 }} />);
    const card = screen.getByLabelText("Structured payload");
    expect(card.tagName).toBe("SECTION");
  });

  it("renders the toggle as a real <button> element", () => {
    // Tab-reachable by default with no tabindex hack.
    render(<StructuredPayloadCard payload={{ a: 1 }} />);
    const toggle = screen.getByRole("button", { name: "Structured payload" });
    expect(toggle.tagName).toBe("BUTTON");
  });
});
