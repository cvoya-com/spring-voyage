import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { UnitNode } from "../aggregate";

vi.mock("../unit-overview-expertise-card", () => ({
  UnitOverviewExpertiseCard: ({ unitId }: { unitId: string }) => (
    <div data-testid="expertise-card-stub" data-unit-id={unitId} />
  ),
}));

const useUnitCostTimeseriesMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnitCostTimeseries: (id: string, window: string, bucket: string) =>
    useUnitCostTimeseriesMock(id, window, bucket),
}));

import UnitOverviewTab from "./unit-overview";

const emptyTimeseries = { data: null, isLoading: false };

describe("UnitOverviewTab", () => {
  it("renders subtree stat tiles rolled up from the node", () => {
    useUnitCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
      cost24h: 2.5,
      msgs24h: 42,
      children: [
        {
          kind: "Agent",
          id: "ada",
          name: "Ada",
          status: "running",
          cost24h: 1.25,
          msgs24h: 10,
        },
      ],
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("tab-unit-overview")).toBeInTheDocument();
    // Messages tile shows the rolled-up total (42 + 10 = 52).
    expect(screen.getByText("52")).toBeInTheDocument();
    // The "Cost (24h)" stat tile renders the aggregated cost.
    expect(screen.getByText("Cost (24h)")).toBeInTheDocument();
  });

  it("mounts the expertise card with the unit id (issue #936)", () => {
    useUnitCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("expertise-card-stub").dataset.unitId,
    ).toBe("engineering");
  });

  it("shows the cost timeseries card with empty state when no data", () => {
    useUnitCostTimeseriesMock.mockReturnValue(emptyTimeseries);
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    expect(
      screen.getByTestId("unit-cost-timeseries-card"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("unit-cost-timeseries-empty"),
    ).toBeInTheDocument();
  });

  it("renders the sparkline when timeseries data is present", () => {
    useUnitCostTimeseriesMock.mockReturnValue({
      data: {
        scope: "unit",
        id: "engineering",
        bucket: "1d",
        from: "2024-01-01T00:00:00Z",
        to: "2024-01-08T00:00:00Z",
        points: [
          { t: "2024-01-01T00:00:00Z", costUsd: 2.0 },
          { t: "2024-01-02T00:00:00Z", costUsd: 3.5 },
          { t: "2024-01-03T00:00:00Z", costUsd: 1.8 },
        ],
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<UnitOverviewTab node={node} path={[node]} />);
    expect(screen.getByTestId("unit-cost-sparkline")).toBeInTheDocument();
  });
});
