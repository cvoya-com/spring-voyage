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

// Mock the API module. Only the policy endpoints are used by the Unit
// branch; mocking keeps us off the network. The Tenant branch reads the
// tenant cloning-policy via `useTenantCloningPolicy` (mocked separately
// below) so it never reaches the api module here.
const getUnitPolicy = vi.fn<(id: string) => Promise<UnitPolicyResponse>>();
const setUnitPolicy =
  vi.fn<(id: string, p: UnitPolicyResponse) => Promise<UnitPolicyResponse>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitPolicy: (id: string) => getUnitPolicy(id),
    setUnitPolicy: (id: string, p: UnitPolicyResponse) =>
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

// Stub the panel bodies the Agent and Tenant branches embed so we can
// assert composition without dragging their network deps into this
// test. Each stub renders a marker the assertions key on.
vi.mock("@/components/agents/agent-initiative-panel", () => ({
  AgentInitiativePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="initiative-panel-stub" data-agent-id={agentId} />
  ),
}));
vi.mock("@/components/agents/agent-cloning-policy-panel", () => ({
  AgentCloningPolicyPanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="agent-cloning-panel-stub" data-agent-id={agentId} />
  ),
}));
vi.mock("@/components/settings/cloning-policy-panel", () => ({
  CloningPolicyPanel: () => <div data-testid="tenant-cloning-panel-stub" />,
}));

import { PoliciesTab } from "./policies-tab";

function renderTab(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<>{node}</>, { wrapper: Wrapper });
}

describe("PoliciesTab — Unit branch (#411 / #2255)", () => {
  beforeEach(() => {
    getUnitPolicy.mockReset();
    setUnitPolicy.mockReset();
    toastMock.mockReset();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders one card per policy dimension plus the effective block and the rollup link", async () => {
    getUnitPolicy.mockResolvedValue({});

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

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
    // Deep-link to /policies is preserved on every subject (#2255 / § 5.9).
    expect(
      screen.getByTestId("tab-unit-policies-link").getAttribute("href"),
    ).toBe("/policies");
  });

  it("shows current allowed / blocked lists when the skill dimension is set", async () => {
    getUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github", "filesystem"], blocked: ["shell"] },
    });

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

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

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

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
      skill: undefined,
      cost: { maxCostPerDay: 25 },
    });

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

    const skillCard = await screen.findByTestId("policies-tab-skill");
    fireEvent.click(
      within(skillCard).getByRole("button", { name: /clear/i }),
    );

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          skill: undefined,
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

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

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

    renderTab(<PoliciesTab kind="Unit" id="engineering" />);

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

describe("PoliciesTab — Agent branch (#2255, was #934 + #534)", () => {
  beforeEach(() => {
    getUnitPolicy.mockReset();
    setUnitPolicy.mockReset();
    toastMock.mockReset();
  });

  it("renders Initiative + Cloning panels scoped to the agent and the rollup link", () => {
    renderTab(<PoliciesTab kind="Agent" id="ada" />);

    expect(screen.getByTestId("tab-agent-policies")).toBeInTheDocument();
    expect(
      screen.getByTestId("initiative-panel-stub").dataset.agentId,
    ).toBe("ada");
    expect(
      screen.getByTestId("agent-cloning-panel-stub").dataset.agentId,
    ).toBe("ada");
    expect(
      screen.getByTestId("tab-agent-policies-link").getAttribute("href"),
    ).toBe("/policies");
  });

  it("does not render the unit dimension cards (cost / model / skill / executionMode)", () => {
    renderTab(<PoliciesTab kind="Agent" id="ada" />);

    // Agent variance: cost / model / skill / executionMode dimensions are
    // declared on the owning unit by design — they must not surface on
    // the agent subject (#2255 / § 5.9).
    expect(screen.queryByTestId("policies-tab-skill")).toBeNull();
    expect(screen.queryByTestId("policies-tab-model")).toBeNull();
    expect(screen.queryByTestId("policies-tab-cost")).toBeNull();
    expect(screen.queryByTestId("policies-tab-execution-mode")).toBeNull();
    expect(screen.queryByTestId("policies-tab-effective")).toBeNull();
  });

  it("does not call the unit-policy endpoint when rendering an agent", () => {
    renderTab(<PoliciesTab kind="Agent" id="ada" />);
    expect(getUnitPolicy).not.toHaveBeenCalled();
  });
});

describe("PoliciesTab — Tenant branch (#2255)", () => {
  beforeEach(() => {
    getUnitPolicy.mockReset();
    setUnitPolicy.mockReset();
    toastMock.mockReset();
  });

  it("renders every dimension as a 'set via CLI' placeholder", () => {
    renderTab(<PoliciesTab kind="Tenant" id="tenant" />);

    expect(screen.getByTestId("tab-tenant-policies")).toBeInTheDocument();
    // Every dimension keeps its slot — the alignment rule forbids hiding
    // options even when the tenant-scope read endpoint hasn't landed
    // yet (§ 5.9).
    expect(screen.getByTestId("policies-tab-skill")).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-model")).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-cost")).toBeInTheDocument();
    expect(
      screen.getByTestId("policies-tab-execution-mode"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("policies-tab-initiative")).toBeInTheDocument();
    // Each placeholder advertises that the editor lives in the CLI.
    expect(screen.getAllByText(/set via CLI/i).length).toBeGreaterThan(0);
  });

  it("embeds the canonical tenant cloning-policy summary panel", () => {
    renderTab(<PoliciesTab kind="Tenant" id="tenant" />);
    expect(
      screen.getByTestId("tab-tenant-policies-cloning"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("tenant-cloning-panel-stub"),
    ).toBeInTheDocument();
  });

  it("preserves the deep-link to /policies for the cross-unit roll-up", () => {
    renderTab(<PoliciesTab kind="Tenant" id="tenant" />);
    expect(
      screen.getByTestId("tab-tenant-policies-link").getAttribute("href"),
    ).toBe("/policies");
  });

  it("does not call the unit-policy endpoint when rendering the tenant", () => {
    renderTab(<PoliciesTab kind="Tenant" id="tenant" />);
    expect(getUnitPolicy).not.toHaveBeenCalled();
  });
});
