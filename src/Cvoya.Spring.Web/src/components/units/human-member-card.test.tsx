import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { HumanResponse, UnitHumanMemberResponse } from "@/lib/api/types";

import { HumanMemberCard } from "./human-member-card";

// `<Link>` resolves to a plain `<a>` in jsdom — matches the convention
// used by the sibling card tests so this fixture stays compatible with
// the rest of the portal-test surface.
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

const getHuman = vi.fn<(id: string) => Promise<HumanResponse>>();
vi.mock("@/lib/api/client", () => ({
  api: {
    getHuman: (id: string) => getHuman(id),
  },
}));

const HUMAN_ID = "11111111-1111-1111-1111-111111111111";

const HUMAN: HumanResponse = {
  id: HUMAN_ID,
  username: "operator",
  displayName: "Operator",
  description: null,
  email: null,
  platformRole: "Operator",
  createdAt: new Date("2024-01-01").toISOString(),
};

function makeRow(
  overrides: Partial<UnitHumanMemberResponse> = {},
): UnitHumanMemberResponse {
  return {
    membershipId: "44444444-4444-4444-4444-444444444444",
    humanId: HUMAN_ID,
    roles: ["tech-lead"],
    expertise: [],
    notifications: [],
    ...overrides,
  };
}

function renderCard(
  row: UnitHumanMemberResponse,
  operatorHumanId: string | null = null,
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <HumanMemberCard
        row={row}
        operatorHumanId={operatorHumanId}
        onEdit={() => {}}
        onRemove={() => {}}
      />
    </QueryClientProvider>,
  );
}

describe("HumanMemberCard (#2464)", () => {
  it("renders a click-through link to the human detail page", async () => {
    getHuman.mockResolvedValue(HUMAN);
    const row = makeRow({ membershipId: "row-a" });
    renderCard(row);

    // Link is rendered synchronously with the row's humanId — even
    // before the displayName query resolves, the navigation target is
    // wired.
    expect(
      screen.getByTestId("unit-human-member-link-row-a"),
    ).toHaveAttribute("href", `/humans/${HUMAN_ID}`);
    // Display name lands once the `useHuman` query resolves.
    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-name-row-a"),
      ).toHaveTextContent("Operator");
    });
  });

  it("keeps the edit / remove buttons clickable above the navigation overlay", async () => {
    getHuman.mockResolvedValue(HUMAN);
    const onEdit = vi.fn();
    const onRemove = vi.fn();
    const row = makeRow({ membershipId: "row-b" });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
    });
    render(
      <QueryClientProvider client={client}>
        <HumanMemberCard
          row={row}
          operatorHumanId={null}
          onEdit={onEdit}
          onRemove={onRemove}
        />
      </QueryClientProvider>,
    );

    await waitFor(() => {
      expect(screen.getByTestId("unit-human-member-edit-row-b")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("unit-human-member-edit-row-b"));
    expect(onEdit).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByTestId("unit-human-member-remove-row-b"));
    expect(onRemove).toHaveBeenCalledTimes(1);
  });

  it("paints the 'You' hint when the row's humanId matches the operator", async () => {
    getHuman.mockResolvedValue(HUMAN);
    const row = makeRow({ membershipId: "row-c" });
    renderCard(row, HUMAN_ID);
    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-you-hint-row-c"),
      ).toBeInTheDocument();
    });
  });
});
