import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { UnitCard } from "./unit-card";

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: React.ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

describe("UnitCard", () => {
  it("renders unit name, status badge, and an open link", () => {
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
      />,
    );

    expect(screen.getByText("Engineering")).toBeInTheDocument();
    expect(screen.getByText("Running")).toBeInTheDocument();
    expect(screen.getByTestId("unit-open-engineering")).toHaveAttribute(
      "href",
      "/explorer/units/engineering",
    );
  });

  it("exposes cross-links to activity, costs, and policies", () => {
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
      />,
    );

    expect(
      screen.getByTestId("unit-link-activity-engineering"),
    ).toHaveAttribute("href", "/explorer/units/engineering?tab=Activity");
    expect(
      screen.getByTestId("unit-link-costs-engineering"),
    ).toHaveAttribute("href", "/explorer/units/engineering?tab=Overview");
    expect(
      screen.getByTestId("unit-link-policies-engineering"),
    ).toHaveAttribute("href", "/explorer/units/engineering?tab=Policies");
  });

  it("defaults status to Draft and shows a sparkline placeholder when no data is available", () => {
    render(
      <UnitCard
        unit={{
          name: "scratch",
          displayName: "Scratch",
          registeredAt: "2026-04-01T00:00:00Z",
        }}
      />,
    );

    expect(screen.getByText("Draft")).toBeInTheDocument();
    expect(screen.getByTestId("unit-sparkline-placeholder")).toBeInTheDocument();
    // No cost badge when cost is not supplied.
    expect(screen.queryByTestId("unit-cost-badge")).toBeNull();
  });

  it("renders a cost badge and sparkline when cost and activitySeries are provided", () => {
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
          cost: 1.23,
          activitySeries: [1, 3, 2, 5],
        }}
      />,
    );

    expect(screen.getByTestId("unit-cost-badge")).toHaveTextContent("$1.23");
    expect(screen.getByTestId("unit-sparkline")).toBeInTheDocument();
  });

  it("exposes a full-card primary link that navigates to the unit detail (#593)", () => {
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
      />,
    );
    const link = screen.getByTestId("unit-card-link-engineering");
    expect(link).toHaveAttribute("href", "/explorer/units/engineering");
    expect(link).toHaveAttribute("aria-label", "Open unit Engineering");
    expect(link.className).toMatch(/after:absolute/);
    expect(link.className).toMatch(/after:inset-0/);
  });

  it("invokes onDelete without navigating when the delete button is clicked", () => {
    const onDelete = vi.fn();
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
        onDelete={onDelete}
      />,
    );
    const btn = screen.getByRole("button", { name: /delete engineering/i });
    fireEvent.click(btn);
    expect(onDelete).toHaveBeenCalledTimes(1);
    expect(onDelete.mock.calls[0][0]).toMatchObject({
      name: "engineering",
      displayName: "Engineering",
    });
  });

  it("renders a CardTabRow footer and hides the legacy cross-links when onOpenTab is provided", () => {
    const onOpenTab = vi.fn();
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
        onOpenTab={onOpenTab}
      />,
    );

    // Legacy cross-links are suppressed.
    expect(screen.queryByTestId("unit-link-activity-engineering")).toBeNull();
    expect(screen.queryByTestId("unit-link-costs-engineering")).toBeNull();
    expect(screen.queryByTestId("unit-link-policies-engineering")).toBeNull();

    // Chip row renders and dispatches (id, tab) on click.
    expect(
      screen.getByTestId("unit-card-tabrow-engineering"),
    ).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("card-tab-chip-activity"));
    expect(onOpenTab).toHaveBeenCalledWith("engineering", "Activity");
  });

  it("keeps the legacy cross-links as fallback when onOpenTab is omitted", () => {
    render(
      <UnitCard
        unit={{
          name: "engineering",
          displayName: "Engineering",
          registeredAt: "2026-04-01T00:00:00Z",
          status: "Running",
        }}
      />,
    );

    expect(
      screen.getByTestId("unit-link-activity-engineering"),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("unit-card-tabrow-engineering")).toBeNull();
  });

  // PR #2390 fixed the footer row; #2441 extends the same pattern to
  // every non-overlay row. The clock + sparkline meta row had no
  // pointer-events handling, so whitespace clicks on it landed on the
  // wrapper div and silently did nothing instead of falling through to
  // the full-card overlay link.
  describe("click-gap regression (#2441)", () => {
    it("marks the clock + sparkline meta row as pointer-events-none", () => {
      render(
        <UnitCard
          unit={{
            name: "engineering",
            displayName: "Engineering",
            registeredAt: "2026-04-01T00:00:00Z",
            status: "Running",
            activitySeries: [1, 2, 3],
          }}
        />,
      );
      // The sparkline lives inside the meta row; walk up to the row
      // wrapper and check it carries `pointer-events-none`.
      const sparkline = screen.getByTestId("unit-sparkline");
      const metaRow = sparkline.parentElement;
      expect(metaRow).not.toBeNull();
      expect(metaRow!.className).toMatch(/pointer-events-none/);
    });

    it("keeps the footer-strip pointer-events fix from PR #2390 intact", () => {
      render(
        <UnitCard
          unit={{
            name: "engineering",
            displayName: "Engineering",
            registeredAt: "2026-04-01T00:00:00Z",
            status: "Running",
          }}
          onDelete={() => {}}
        />,
      );
      // Footer wrapper retains `pointer-events-none`; the Open link
      // and the Delete button retain their own `pointer-events-auto`.
      const open = screen.getByTestId("unit-open-engineering");
      expect(open.closest(".pointer-events-none")).not.toBeNull();
      expect(open.className).toMatch(/pointer-events-auto/);
      const del = screen.getByTestId("unit-delete-engineering");
      expect(del.className).toMatch(/pointer-events-auto/);
      // Cross-link icons keep `pointer-events-auto`.
      const activity = screen.getByTestId("unit-link-activity-engineering");
      expect(activity.className).toMatch(/pointer-events-auto/);
    });
  });
});
