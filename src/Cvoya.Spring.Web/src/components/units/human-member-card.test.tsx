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
const listCallerHumans = vi.fn(async () => [] as unknown[]);
const updateHumanBinding = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: {
    getHuman: (id: string) => getHuman(id),
    listCallerHumans: () => listCallerHumans(),
    updateHumanBinding: (humanId: string, body: unknown) =>
      updateHumanBinding(humanId, body),
  },
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
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
  operatorTenantUserId: string | null = null,
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <HumanMemberCard
        row={row}
        operatorHumanId={operatorHumanId}
        operatorTenantUserId={operatorTenantUserId}
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
          operatorTenantUserId={null}
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

  // -------------------------------------------------------------------------
  // ADR-0062 § 1 — "Claim this Human" affordance.
  // -------------------------------------------------------------------------

  it("hides the claim button when no caller TenantUser is available", async () => {
    getHuman.mockResolvedValue(HUMAN);
    listCallerHumans.mockResolvedValue([]);
    const row = makeRow({ membershipId: "row-d" });
    renderCard(row, null, null);
    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-edit-row-d"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("unit-human-member-claim-row-d"),
    ).not.toBeInTheDocument();
  });

  it("hides the claim button when the caller is already bound to the Hat", async () => {
    getHuman.mockResolvedValue(HUMAN);
    listCallerHumans.mockResolvedValue([
      { humanId: HUMAN_ID, displayName: "Operator", isPrimary: true, memberships: [] },
    ]);
    const row = makeRow({ membershipId: "row-e" });
    renderCard(row, HUMAN_ID, "tu-1");
    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-edit-row-e"),
      ).toBeInTheDocument();
    });
    // Settle the useCallerHumans query.
    await waitFor(() => {
      expect(listCallerHumans).toHaveBeenCalled();
    });
    expect(
      screen.queryByTestId("unit-human-member-claim-row-e"),
    ).not.toBeInTheDocument();
  });

  it("renders the claim button when the caller is unbound and patches on click", async () => {
    getHuman.mockResolvedValue(HUMAN);
    listCallerHumans.mockResolvedValue([]);
    updateHumanBinding.mockResolvedValue(HUMAN);
    const row = makeRow({ membershipId: "row-f" });
    renderCard(row, null, "tu-1");

    const claim = await waitFor(() =>
      screen.getByTestId("unit-human-member-claim-row-f"),
    );
    fireEvent.click(claim);
    await waitFor(() => {
      expect(updateHumanBinding).toHaveBeenCalledWith(HUMAN_ID, {
        tenantUserId: "tu-1",
      });
    });
  });
});
