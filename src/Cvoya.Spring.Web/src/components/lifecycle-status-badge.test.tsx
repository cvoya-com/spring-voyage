/**
 * Tests for the shared `<LifecycleStatusBadge>` component (#2372).
 *
 * Covers the 7-state vocabulary, case-insensitive normalisation (the tree
 * carries lowercase values, API responses carry PascalCase), and the
 * coloured-dot variant exposed for use in tree rows / detail-pane headers.
 */

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import {
  LifecycleStatusBadge,
  LifecycleStatusDot,
  normaliseLifecycleStatus,
} from "./lifecycle-status-badge";

describe("normaliseLifecycleStatus", () => {
  it("passes PascalCase values through", () => {
    expect(normaliseLifecycleStatus("Running")).toBe("Running");
    expect(normaliseLifecycleStatus("Validating")).toBe("Validating");
  });

  it("lifts lowercase tree values to PascalCase", () => {
    expect(normaliseLifecycleStatus("running")).toBe("Running");
    expect(normaliseLifecycleStatus("stopping")).toBe("Stopping");
    expect(normaliseLifecycleStatus("draft")).toBe("Draft");
  });

  it("collapses null / unknown to Draft", () => {
    expect(normaliseLifecycleStatus(null)).toBe("Draft");
    expect(normaliseLifecycleStatus(undefined)).toBe("Draft");
    expect(normaliseLifecycleStatus("nonsense")).toBe("Draft");
  });
});

describe("LifecycleStatusBadge", () => {
  it("renders the label for every known status", () => {
    const all = [
      "Draft",
      "Validating",
      "Stopped",
      "Starting",
      "Running",
      "Stopping",
      "Error",
    ] as const;
    for (const status of all) {
      const { container, unmount } = render(
        <LifecycleStatusBadge status={status} />,
      );
      expect(container.textContent).toContain(status);
      const badge = container.querySelector("[data-lifecycle-status]");
      expect(badge?.getAttribute("data-lifecycle-status")).toBe(status);
      unmount();
    }
  });

  it("accepts a lowercase tree value and renders the PascalCase label", () => {
    render(<LifecycleStatusBadge status="running" testId="badge" />);
    const badge = screen.getByTestId("badge");
    expect(badge).toHaveTextContent("Running");
    expect(badge.getAttribute("data-lifecycle-status")).toBe("Running");
  });

  it("hides the leading dot when showDot is false", () => {
    const { container } = render(
      <LifecycleStatusBadge status="Running" showDot={false} testId="badge" />,
    );
    const badge = container.querySelector('[data-testid="badge"]');
    // The dot is the only `<span aria-hidden>` child — when hidden the badge
    // contains only the label.
    expect(badge?.querySelector("span[aria-hidden]")).toBeNull();
  });

  it("collapses an unknown status to Draft rather than rendering an empty pill", () => {
    render(<LifecycleStatusBadge status="bogus" testId="badge" />);
    expect(screen.getByTestId("badge")).toHaveTextContent("Draft");
  });
});

describe("LifecycleStatusDot", () => {
  it("emits the lifecycle status attribute for the matching class", () => {
    render(<LifecycleStatusDot status="Error" testId="dot" />);
    const dot = screen.getByTestId("dot");
    expect(dot.getAttribute("data-lifecycle-status")).toBe("Error");
    expect(dot.className).toContain("bg-destructive");
  });

  it("normalises a lowercase tree value", () => {
    render(<LifecycleStatusDot status="stopped" testId="dot" />);
    const dot = screen.getByTestId("dot");
    expect(dot.getAttribute("data-lifecycle-status")).toBe("Stopped");
  });
});
