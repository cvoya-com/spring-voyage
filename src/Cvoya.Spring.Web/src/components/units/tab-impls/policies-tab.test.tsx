import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type { UnitPolicyResponse } from "@/lib/api/types";

// Mock the API module. Only the policy endpoints are used by the tab;
// mocking keeps us off the network.
const getUnitPolicy = vi.fn<(id: string) => Promise<UnitPolicyResponse>>();
const setUnitPolicy =
  vi.fn<
    (id: string, p: UnitPolicyResponse | null) => Promise<UnitPolicyResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitPolicy: (id: string) => getUnitPolicy(id),
    setUnitPolicy: (id: string, p: UnitPolicyResponse | null) =>
      setUnitPolicy(id, p),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
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

import { PoliciesTab } from "./policies-tab";

function renderTab(id: string) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<PoliciesTab unitId={id} />, { wrapper: Wrapper });
}

describe("PoliciesTab", () => {
  beforeEach(() => {
    getUnitPolicy.mockReset();
    setUnitPolicy.mockReset();
    toastMock.mockReset();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders one card per policy dimension plus the effective block", async () => {
    getUnitPolicy.mockResolvedValue({});

    renderTab("engineering");

    await waitFor(() => {
      expect(screen.getByTestId("policies-tab-skill")).toBeInTheDocument();
    });
    expect(screen.getByTestId("policies-tab-model")).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-cost")).toBeInTheDocument();
    expect(
      screen.getByTestId("policies-tab-execution-mode"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-initiative")).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-effective")).toBeInTheDocument();
  });

  it("shows current allowed / blocked lists when the skill dimension is set", async () => {
    getUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github", "filesystem"], blocked: ["shell"] },
    });

    renderTab("engineering");

    const card = await screen.findByTestId("policies-tab-skill");
    expect(within(card).getByText("github")).toBeInTheDocument();
    expect(within(card).getByText("filesystem")).toBeInTheDocument();
    expect(within(card).getByText("shell")).toBeInTheDocument();
  });

  it("merges Skill edits into the existing policy via PUT", async () => {
    getUnitPolicy.mockResolvedValue({
      // The cost dimension must be carried through on a Skill edit —
      // per-dimension sets never wipe siblings.
      cost: { maxCostPerDay: 25 },
    });
    setUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github"], blocked: null },
      cost: { maxCostPerDay: 25 },
    });

    renderTab("engineering");

    const skillCard = await screen.findByTestId("policies-tab-skill");
    fireEvent.click(within(skillCard).getByRole("button", { name: /edit/i }));

    const dialog = await screen.findByTestId("skill-policy-dialog");
    const [allowedInput] = within(dialog).getAllByRole("textbox");
    fireEvent.change(allowedInput, { target: { value: "github" } });

    fireEvent.click(
      within(
        screen.getByTestId("skill-policy-dialog-footer"),
      ).getByRole("button", { name: /^save$/i }),
    );

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          skill: { allowed: ["github"], blocked: null },
          cost: { maxCostPerDay: 25 },
        }),
      );
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Policy saved" }),
    );
  });

  it("clearing a dimension issues a PUT that nulls only that slot", async () => {
    getUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github"], blocked: null },
      cost: { maxCostPerDay: 25 },
    });
    setUnitPolicy.mockResolvedValue({
      skill: null,
      cost: { maxCostPerDay: 25 },
    });

    renderTab("engineering");

    const skillCard = await screen.findByTestId("policies-tab-skill");
    fireEvent.click(
      within(skillCard).getByRole("button", { name: /clear/i }),
    );

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          skill: null,
          cost: { maxCostPerDay: 25 },
        }),
      );
    });
  });

  it("saves cost caps as numeric values", async () => {
    getUnitPolicy.mockResolvedValue({});
    setUnitPolicy.mockResolvedValue({
      cost: { maxCostPerDay: 25, maxCostPerHour: 5 },
    });

    renderTab("engineering");

    const card = await screen.findByTestId("policies-tab-cost");
    fireEvent.click(within(card).getByRole("button", { name: /edit/i }));

    const dialog = await screen.findByTestId("cost-policy-dialog");
    const inputs = within(dialog).getAllByRole("spinbutton");
    // Per invocation, per hour, per day — in that order.
    fireEvent.change(inputs[1], { target: { value: "5" } });
    fireEvent.change(inputs[2], { target: { value: "25" } });

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          cost: {
            maxCostPerInvocation: undefined,
            maxCostPerHour: 5,
            maxCostPerDay: 25,
          },
        }),
      );
    });
  });

  it("saves initiative policy with maxLevel and require-approval flags", async () => {
    getUnitPolicy.mockResolvedValue({});
    setUnitPolicy.mockResolvedValue({
      initiative: {
        maxLevel: "Proactive",
        requireUnitApproval: true,
        allowedActions: null,
        blockedActions: ["agent.spawn"],
      },
    });

    renderTab("engineering");

    const card = await screen.findByTestId("policies-tab-initiative");
    fireEvent.click(within(card).getByRole("button", { name: /edit/i }));

    const dialog = await screen.findByTestId("initiative-policy-dialog");
    fireEvent.change(within(dialog).getByRole("combobox"), {
      target: { value: "Proactive" },
    });
    fireEvent.click(within(dialog).getByRole("checkbox"));
    const [, blockedInput] = within(dialog).getAllByRole("textbox");
    fireEvent.change(blockedInput, { target: { value: "agent.spawn" } });

    fireEvent.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          initiative: expect.objectContaining({
            maxLevel: "Proactive",
            requireUnitApproval: true,
            blockedActions: ["agent.spawn"],
          }),
        }),
      );
    });
  });
});
