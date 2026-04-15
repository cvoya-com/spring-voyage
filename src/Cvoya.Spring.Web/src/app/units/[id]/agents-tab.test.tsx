import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type {
  AgentResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";

// Mock the API module: only the calls the tab actually makes need to be
// defined. Anything else left undefined would throw if accidentally called.
const listUnitMemberships =
  vi.fn<(unitId: string) => Promise<UnitMembershipResponse[]>>();
const listAgents = vi.fn<() => Promise<AgentResponse[]>>();
const upsertUnitMembership = vi.fn();
const deleteUnitMembership = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listUnitMemberships: (u: string) => listUnitMemberships(u),
    listAgents: () => listAgents(),
    upsertUnitMembership: (...args: unknown[]) =>
      upsertUnitMembership(...args),
    deleteUnitMembership: (...args: unknown[]) =>
      deleteUnitMembership(...args),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { AgentsTab } from "./agents-tab";

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: "actor-id",
    name: "ada",
    displayName: "Ada",
    description: "",
    role: null,
    registeredAt: new Date().toISOString(),
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: null,
    ...overrides,
  } as AgentResponse;
}

function makeMembership(
  overrides: Partial<UnitMembershipResponse> = {},
): UnitMembershipResponse {
  const now = new Date().toISOString();
  return {
    unitId: "engineering",
    agentAddress: "ada",
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    createdAt: now,
    updatedAt: now,
    ...overrides,
  };
}

describe("AgentsTab", () => {
  beforeEach(() => {
    listUnitMemberships.mockReset();
    listAgents.mockReset();
    upsertUnitMembership.mockReset();
    deleteUnitMembership.mockReset();
    toastMock.mockReset();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows the empty state and 'Add agent' button when no memberships exist", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([makeAgent({ name: "hopper" })]);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByText(/No agents assigned to this unit yet/i),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByRole("button", { name: /add agent/i }),
    ).toBeEnabled();
  });

  it("lists memberships with display names and per-membership config", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({
        agentAddress: "ada",
        model: "claude-sonnet-4-20250514",
        specialty: "reviewer",
        enabled: true,
      }),
    ]);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });
    expect(screen.getByText(/reviewer/i)).toBeInTheDocument();
    expect(screen.getByText(/claude-sonnet-4-20250514/)).toBeInTheDocument();
  });

  it("opens the Add dialog, submits the correct PUT payload, and refreshes the row", async () => {
    const hopper = makeAgent({ name: "hopper", displayName: "Hopper" });
    listAgents.mockResolvedValue([hopper]);
    listUnitMemberships.mockResolvedValue([]);

    const saved = makeMembership({
      agentAddress: "hopper",
      model: "claude-opus-4-20250514",
      specialty: "architect",
      enabled: true,
      executionMode: "OnDemand",
    });
    upsertUnitMembership.mockResolvedValue(saved);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /add agent/i }),
      ).toBeEnabled();
    });
    fireEvent.click(screen.getByRole("button", { name: /add agent/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Add agent to unit/i)).toBeInTheDocument();

    fireEvent.change(within(dialog).getByLabelText(/^Agent$/i), {
      target: { value: "hopper" },
    });
    fireEvent.change(within(dialog).getByLabelText(/^Model$/i), {
      target: { value: "claude-opus-4-20250514" },
    });
    fireEvent.change(within(dialog).getByLabelText(/^Specialty$/i), {
      target: { value: "architect" },
    });
    fireEvent.change(within(dialog).getByLabelText(/Execution mode/i), {
      target: { value: "OnDemand" },
    });

    fireEvent.click(within(dialog).getByRole("button", { name: /add agent/i }));

    await waitFor(() => {
      expect(upsertUnitMembership).toHaveBeenCalledWith(
        "engineering",
        "hopper",
        {
          model: "claude-opus-4-20250514",
          specialty: "architect",
          enabled: true,
          executionMode: "OnDemand",
        },
      );
    });

    await waitFor(() => {
      expect(screen.queryByRole("dialog")).toBeNull();
    });
    expect(screen.getByText("Hopper")).toBeInTheDocument();
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Agent added" }),
    );
  });

  it("pre-populates the Edit dialog from the existing membership", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({
        agentAddress: "ada",
        model: "claude-opus-4-20250514",
        specialty: "reviewer",
        enabled: false,
        executionMode: "OnDemand",
      }),
    ]);

    const updated = makeMembership({
      agentAddress: "ada",
      model: "claude-sonnet-4-20250514",
      specialty: "reviewer",
      enabled: false,
      executionMode: "OnDemand",
    });
    upsertUnitMembership.mockResolvedValue(updated);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /Edit Ada/i }));

    const dialog = await screen.findByRole("dialog");
    // The agent field is read-only in edit mode; the header calls it out.
    expect(within(dialog).getByText(/Edit membership/i)).toBeInTheDocument();
    expect(
      (within(dialog).getByLabelText(/^Model$/i) as HTMLSelectElement).value,
    ).toBe("claude-opus-4-20250514");
    expect(
      (within(dialog).getByLabelText(/^Specialty$/i) as HTMLInputElement)
        .value,
    ).toBe("reviewer");
    expect(
      (within(dialog).getByLabelText(/Execution mode/i) as HTMLSelectElement)
        .value,
    ).toBe("OnDemand");
    expect(
      (within(dialog).getByLabelText(/Enabled/i) as HTMLInputElement).checked,
    ).toBe(false);

    fireEvent.change(within(dialog).getByLabelText(/^Model$/i), {
      target: { value: "claude-sonnet-4-20250514" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(upsertUnitMembership).toHaveBeenCalledWith(
        "engineering",
        "ada",
        expect.objectContaining({ model: "claude-sonnet-4-20250514" }),
      );
    });
  });

  it("does not call DELETE when the user cancels the confirm dialog", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({ agentAddress: "ada" }),
    ]);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /Remove Ada/i }));

    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).getByText(/Remove agent from unit/i),
    ).toBeInTheDocument();

    fireEvent.click(within(dialog).getByRole("button", { name: /cancel/i }));

    await waitFor(() => {
      expect(screen.queryByRole("dialog")).toBeNull();
    });
    expect(deleteUnitMembership).not.toHaveBeenCalled();
    expect(screen.getByText("Ada")).toBeInTheDocument();
  });

  it("calls DELETE on confirm and removes the row", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({ agentAddress: "ada" }),
    ]);
    deleteUnitMembership.mockResolvedValue(undefined);

    render(<AgentsTab unitId="engineering" />);

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /Remove Ada/i }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: /^remove$/i }));

    await waitFor(() => {
      expect(deleteUnitMembership).toHaveBeenCalledWith(
        "engineering",
        "ada",
      );
    });

    await waitFor(() => {
      expect(screen.queryByText("Ada")).toBeNull();
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Agent removed" }),
    );
  });

  it("keeps the Add dialog open and surfaces the error on 400", async () => {
    const hopper = makeAgent({ name: "hopper", displayName: "Hopper" });
    listAgents.mockResolvedValue([hopper]);
    listUnitMemberships.mockResolvedValue([]);

    upsertUnitMembership.mockRejectedValue(
      new Error("API error 400: Bad Request — model is required"),
    );

    render(<AgentsTab unitId="engineering" />);
    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /add agent/i }),
      ).toBeEnabled();
    });
    fireEvent.click(screen.getByRole("button", { name: /add agent/i }));

    const dialog = await screen.findByRole("dialog");
    fireEvent.change(within(dialog).getByLabelText(/^Agent$/i), {
      target: { value: "hopper" },
    });

    await act(async () => {
      fireEvent.click(
        within(dialog).getByRole("button", { name: /add agent/i }),
      );
    });

    // Dialog must remain open so the user can fix the input.
    expect(screen.getByRole("dialog")).toBeInTheDocument();
    expect(screen.getByRole("alert")).toHaveTextContent(/model is required/);
  });

  it("only lists agents not already members of THIS unit in the picker", async () => {
    const ada = makeAgent({
      name: "ada",
      displayName: "Ada",
      parentUnit: "engineering",
    });
    const hopper = makeAgent({
      name: "hopper",
      displayName: "Hopper",
      parentUnit: "marketing",
    });
    listAgents.mockResolvedValue([ada, hopper]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({ agentAddress: "ada" }),
    ]);

    render(<AgentsTab unitId="engineering" />);
    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /add agent/i }));
    const dialog = await screen.findByRole("dialog");

    // ada is already a member → excluded; hopper belongs to a different unit
    // but is eligible under M:N.
    const agentSelect = within(dialog).getByLabelText(
      /^Agent$/i,
    ) as HTMLSelectElement;
    const options = Array.from(agentSelect.options).map((o) => o.value);
    expect(options).toContain("hopper");
    expect(options).not.toContain("ada");
  });
});
