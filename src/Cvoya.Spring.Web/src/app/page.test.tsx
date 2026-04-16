import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { DashboardSummary } from "@/lib/api/types";

const getDashboardSummary =
  vi.fn<() => Promise<DashboardSummary>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getDashboardSummary: () => getDashboardSummary(),
  },
}));

vi.mock("@/hooks/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

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

import DashboardPage from "./page";

function makeSummary(
  overrides: Partial<DashboardSummary> = {},
): DashboardSummary {
  return {
    unitCount: 2,
    unitsByStatus: { Draft: 1, Running: 1 },
    agentCount: 3,
    recentActivity: [],
    totalCost: 42.5,
    ...overrides,
  };
}

describe("DashboardPage", () => {
  beforeEach(() => {
    getDashboardSummary.mockReset();
  });

  it("renders unit count and agent count from summary", async () => {
    getDashboardSummary.mockResolvedValue(makeSummary());

    render(<DashboardPage />);

    await waitFor(() => {
      expect(screen.getByText("2")).toBeInTheDocument();
    });
    expect(screen.getByText("3")).toBeInTheDocument();
    expect(screen.getByText("$42.50")).toBeInTheDocument();
  });

  it("renders status badges from unitsByStatus", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({ unitsByStatus: { Draft: 1, Running: 1 } }),
    );

    render(<DashboardPage />);

    await waitFor(() => {
      expect(screen.getByText("Draft: 1")).toBeInTheDocument();
    });
    expect(screen.getByText("Running: 1")).toBeInTheDocument();
  });

  it("does not render status section when no units", async () => {
    getDashboardSummary.mockResolvedValue(
      makeSummary({ unitCount: 0, unitsByStatus: {} }),
    );

    render(<DashboardPage />);

    await waitFor(() => {
      expect(screen.getByText("0")).toBeInTheDocument();
    });
    expect(screen.queryByText("Units by Status")).not.toBeInTheDocument();
  });
});
