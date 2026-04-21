import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

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

const useOwnMock = vi.fn();
const useAggregatedMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnitOwnExpertise: (id: string) => useOwnMock(id),
  useUnitAggregatedExpertise: (id: string) => useAggregatedMock(id),
}));

import { UnitOverviewExpertiseCard } from "./unit-overview-expertise-card";

describe("UnitOverviewExpertiseCard (issue #936)", () => {
  it("shows an empty state when no expertise is declared yet", () => {
    useOwnMock.mockReturnValueOnce({ data: [], isPending: false });
    useAggregatedMock.mockReturnValueOnce({
      data: { entries: [] },
      isPending: false,
    });
    render(<UnitOverviewExpertiseCard unitId="engineering" />);
    expect(
      screen.getByTestId("unit-overview-expertise-card"),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/No expertise declared on this unit/i),
    ).toBeInTheDocument();
  });

  it("renders own + deduped aggregated chips and a Manage deep-link", () => {
    useOwnMock.mockReturnValueOnce({
      data: [{ name: "typescript", level: "expert" }],
      isPending: false,
    });
    useAggregatedMock.mockReturnValueOnce({
      data: {
        entries: [
          { domain: { name: "typescript", level: "expert" }, origins: [] },
          { domain: { name: "rust", level: "intermediate" }, origins: [] },
          { domain: { name: "rust", level: "intermediate" }, origins: [] },
        ],
      },
      isPending: false,
    });
    render(<UnitOverviewExpertiseCard unitId="engineering" />);

    // Own row shows "typescript · expert" + aggregated also shows it (so ≥1 match).
    expect(screen.getAllByText(/typescript · expert/i).length).toBeGreaterThan(
      0,
    );

    // Aggregated row dedupes rust (2 origins collapse to 1 chip).
    expect(screen.getByText(/Subtree \(2 unique\)/i)).toBeInTheDocument();

    const manage = screen.getByTestId(
      "unit-overview-expertise-manage",
    ) as HTMLAnchorElement;
    expect(manage.getAttribute("href")).toBe(
      "/units?node=engineering&tab=Config&subtab=Expertise",
    );
  });

  it("shows a skeleton while queries are pending", () => {
    useOwnMock.mockReturnValueOnce({ data: undefined, isPending: true });
    useAggregatedMock.mockReturnValueOnce({
      data: undefined,
      isPending: true,
    });
    const { container } = render(
      <UnitOverviewExpertiseCard unitId="engineering" />,
    );
    // Skeleton renders a bare <div> — just check we haven't rendered chips.
    expect(container.querySelector(".h-16")).toBeTruthy();
  });
});
