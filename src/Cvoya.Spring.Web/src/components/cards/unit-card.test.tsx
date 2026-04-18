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
      "/units/engineering",
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
    ).toHaveAttribute("href", "/units/engineering?tab=activity");
    expect(
      screen.getByTestId("unit-link-costs-engineering"),
    ).toHaveAttribute("href", "/units/engineering?tab=costs");
    expect(
      screen.getByTestId("unit-link-policies-engineering"),
    ).toHaveAttribute("href", "/units/engineering?tab=policies");
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
    expect(link).toHaveAttribute("href", "/units/engineering");
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
});
