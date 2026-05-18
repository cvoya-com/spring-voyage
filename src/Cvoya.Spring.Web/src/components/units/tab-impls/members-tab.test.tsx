import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { TreeNode, UnitNode } from "@/components/units/aggregate";
import type {
  AgentResponse,
  HumanResponse,
  InstalledModelProviderResponse,
  UnitHumanMemberResponse,
  UnitMembershipResponse,
  UserProfileResponse,
} from "@/lib/api/types";

interface AgentCreateDialogMockProps {
  unitId: string;
  unitDisplayName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const agentCreateDialogMock = vi.hoisted(() => vi.fn());

// Mock the API module. The Members tab now exercises the human team-
// role surface in addition to the existing agent-membership surface,
// so the mocks span both endpoint families.
const listUnitMemberships =
  vi.fn<(unitId: string) => Promise<UnitMembershipResponse[]>>();
const listAgents = vi.fn<() => Promise<AgentResponse[]>>();
const upsertUnitMembership = vi.fn();
const deleteUnitMembership = vi.fn();
const listUnitHumanMembers =
  vi.fn<(unitId: string) => Promise<UnitHumanMemberResponse[]>>();
const addUnitHumanMember = vi.fn();
const updateUnitHumanMember = vi.fn();
const removeUnitHumanMember = vi.fn();
const getHuman = vi.fn<(id: string) => Promise<HumanResponse>>();
const getCurrentUser = vi.fn<() => Promise<UserProfileResponse>>();
// ADR-0038: MembershipDialog sources its Model dropdown from the
// model-providers endpoint via `useModelProviders`, so this stub is
// required whenever the edit dialog opens.
const listModelProviders =
  vi.fn<() => Promise<InstalledModelProviderResponse[]>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listUnitMemberships: (u: string) => listUnitMemberships(u),
    listAgents: () => listAgents(),
    upsertUnitMembership: (...args: unknown[]) =>
      upsertUnitMembership(...args),
    deleteUnitMembership: (...args: unknown[]) =>
      deleteUnitMembership(...args),
    listUnitHumanMembers: (u: string) => listUnitHumanMembers(u),
    addUnitHumanMember: (...args: unknown[]) => addUnitHumanMember(...args),
    updateUnitHumanMember: (...args: unknown[]) =>
      updateUnitHumanMember(...args),
    removeUnitHumanMember: (...args: unknown[]) =>
      removeUnitHumanMember(...args),
    getHuman: (id: string) => getHuman(id),
    getCurrentUser: () => getCurrentUser(),
    listModelProviders: () => listModelProviders(),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

vi.mock("@/components/agents/create-dialog", () => ({
  AgentCreateDialog: (props: AgentCreateDialogMockProps) => {
    agentCreateDialogMock(props);
    if (!props.open) return null;
    return (
      <div
        role="dialog"
        aria-label="Create agent"
        data-testid="agent-create-dialog"
        data-unit-id={props.unitId}
        data-unit-display-name={props.unitDisplayName}
      >
        <button
          type="button"
          onClick={() => props.onOpenChange(false)}
        >
          Close create dialog
        </button>
      </div>
    );
  },
}));

import { MembersTab } from "./members-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const OPERATOR_GUID = "11111111-1111-1111-1111-111111111111";

const OPERATOR_PROFILE: UserProfileResponse = {
  userId: "savas",
  displayName: "Savas",
  id: OPERATOR_GUID,
  address: `human:${OPERATOR_GUID.replace(/-/g, "")}`,
};

const OPERATOR_HUMAN: HumanResponse = {
  id: OPERATOR_GUID,
  username: "savas",
  displayName: "Savas",
  email: "savas@example.com",
  platformRole: "Owner",
  createdAt: new Date("2024-01-01").toISOString(),
};

function renderMembersTab(
  unitId: string,
  unitDisplayName = "Engineering",
  childNodes: readonly TreeNode[] = [],
) {
  return render(
    <Wrapper>
      <MembersTab
        unitId={unitId}
        unitDisplayName={unitDisplayName}
        childNodes={childNodes}
      />
    </Wrapper>,
  );
}

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
  const base = {
    unitId: "engineering",
    agentAddress: "ada",
    agentDisplayName:
      overrides.agentDisplayName ?? overrides.agentAddress ?? "ada",
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto" as const,
    createdAt: now,
    updatedAt: now,
    isPrimary: false,
    ...overrides,
  };
  return {
    ...base,
    member: overrides.member ?? `agent://${base.agentAddress}`,
  };
}

function makeHumanMember(
  overrides: Partial<UnitHumanMemberResponse> = {},
): UnitHumanMemberResponse {
  return {
    membershipId: "44444444-4444-4444-4444-444444444444",
    humanId: OPERATOR_GUID,
    role: "tech-lead",
    expertise: [],
    notifications: [],
    ...overrides,
  };
}

// ADR-0038: minimal tenant-installed-provider fixture modelled after
// `InstalledModelProviderResponse`.
const DEFAULT_PROVIDERS = [
  {
    id: "anthropic",
    displayName: "Anthropic",
    models: ["claude-sonnet-4-6", "claude-opus-4-7"],
    defaultModel: "claude-sonnet-4-6",
  },
  {
    id: "openai",
    displayName: "OpenAI",
    models: ["gpt-4o", "gpt-4o-mini"],
    defaultModel: "gpt-4o",
  },
] as unknown as InstalledModelProviderResponse[];

describe("MembersTab (#2270 / #2427)", () => {
  beforeEach(() => {
    listUnitMemberships.mockReset();
    listAgents.mockReset();
    upsertUnitMembership.mockReset();
    deleteUnitMembership.mockReset();
    listUnitHumanMembers.mockReset();
    addUnitHumanMember.mockReset();
    updateUnitHumanMember.mockReset();
    removeUnitHumanMember.mockReset();
    getHuman.mockReset();
    getCurrentUser.mockReset();
    listModelProviders.mockReset();
    toastMock.mockReset();
    agentCreateDialogMock.mockReset();
    listModelProviders.mockResolvedValue(DEFAULT_PROVIDERS);
    // Default benign stubs so unrelated tests do not throw on the
    // human surface; tests that exercise the human path override.
    listUnitHumanMembers.mockResolvedValue([]);
    getHuman.mockResolvedValue(OPERATOR_HUMAN);
    getCurrentUser.mockResolvedValue(OPERATOR_PROFILE);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows the combined empty state and an Add human affordance", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(screen.getByTestId("unit-members-empty")).toBeInTheDocument();
    });
    // The empty-state CTA opens the Add human dialog.
    fireEvent.click(screen.getByTestId("unit-members-empty-add"));
    expect(
      await screen.findByText("Add human member"),
    ).toBeInTheDocument();
  });

  it("opens AgentCreateDialog with the current unit and refreshes memberships on close", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);

    renderMembersTab("engineering", "Engineering Team");

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /add agent/i }),
      ).toBeEnabled();
    });

    fireEvent.click(screen.getByRole("button", { name: /add agent/i }));

    const dialog = await screen.findByTestId("agent-create-dialog");
    expect(dialog).toHaveAttribute("data-unit-id", "engineering");
    expect(dialog).toHaveAttribute(
      "data-unit-display-name",
      "Engineering Team",
    );

    fireEvent.click(
      within(dialog).getByRole("button", {
        name: /close create dialog/i,
      }),
    );

    await waitFor(() => {
      expect(screen.queryByTestId("agent-create-dialog")).toBeNull();
    });
    await waitFor(() => {
      expect(listUnitMemberships).toHaveBeenCalledTimes(2);
      expect(listAgents).toHaveBeenCalledTimes(2);
    });
  });

  it("lists agent memberships with display names and per-membership config", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({
        agentAddress: "ada",
        model: "claude-sonnet-4-6",
        specialty: "reviewer",
        enabled: true,
      }),
    ]);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });
    expect(screen.getByText(/reviewer/i)).toBeInTheDocument();
    expect(screen.getByText(/claude-sonnet-4-6/)).toBeInTheDocument();
    expect(
      screen.getByTestId("unit-members-agents-section"),
    ).toBeInTheDocument();
  });

  it("renders sub-unit cards from the active node's children", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    const backend: UnitNode = {
      kind: "Unit",
      id: "engineering/backend",
      name: "Backend",
      status: "running",
    };

    renderMembersTab("engineering", "Engineering", [backend]);

    await waitFor(() => {
      expect(
        screen.getByTestId("unit-members-sub-units-section"),
      ).toBeInTheDocument();
    });
    expect(screen.getByText("Backend")).toBeInTheDocument();
  });

  it("renders one human-member card per (humanId, role) row with role chip", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    listUnitHumanMembers.mockResolvedValue([
      makeHumanMember({
        membershipId: "row-1",
        role: "tech-lead",
        expertise: ["databases"],
        notifications: ["pull-requests"],
      }),
      makeHumanMember({
        membershipId: "row-2",
        role: "reviewer",
      }),
    ]);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(screen.getByTestId("unit-human-member-card-row-1")).toBeInTheDocument();
      expect(screen.getByTestId("unit-human-member-card-row-2")).toBeInTheDocument();
    });
    expect(screen.getByTestId("unit-human-member-role-row-1")).toHaveTextContent("tech-lead");
    expect(screen.getByTestId("unit-human-member-role-row-2")).toHaveTextContent("reviewer");
    expect(screen.getByTestId("unit-human-member-expertise-row-1-0")).toHaveTextContent("databases");
  });

  it("paints the You hint when the row matches the operator's Human id", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    listUnitHumanMembers.mockResolvedValue([
      makeHumanMember({ membershipId: "row-mine", humanId: OPERATOR_GUID }),
    ]);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-you-hint-row-mine"),
      ).toBeInTheDocument();
    });
  });

  it("submits a POST with the operator's Human id when adding a human member", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    addUnitHumanMember.mockResolvedValue(
      makeHumanMember({ membershipId: "new" }),
    );

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /add human member/i }),
      ).toBeEnabled();
    });

    fireEvent.click(
      screen.getByRole("button", { name: /add human member/i }),
    );

    const dialog = await screen.findByRole("dialog");
    // The Human field is auto-filled with the operator and locked.
    expect(
      within(dialog).getByTestId("human-member-dialog-you-hint"),
    ).toBeInTheDocument();

    fireEvent.change(within(dialog).getByTestId("human-member-dialog-role"), {
      target: { value: "tech-lead" },
    });
    fireEvent.change(
      within(dialog).getByTestId("human-member-dialog-expertise"),
      { target: { value: "databases, security" } },
    );
    fireEvent.change(
      within(dialog).getByTestId("human-member-dialog-notifications"),
      { target: { value: "pull-requests" } },
    );

    fireEvent.click(
      within(dialog).getByTestId("human-member-dialog-submit"),
    );

    await waitFor(() => {
      expect(addUnitHumanMember).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          humanId: OPERATOR_GUID,
          role: "tech-lead",
          expertise: ["databases", "security"],
          notifications: ["pull-requests"],
        }),
      );
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Member added" }),
    );
  });

  it("PATCHes expertise / notifications when editing a human member", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    listUnitHumanMembers.mockResolvedValue([
      makeHumanMember({
        membershipId: "row-edit",
        humanId: OPERATOR_GUID,
        role: "tech-lead",
        expertise: ["databases"],
        notifications: ["pull-requests"],
      }),
    ]);
    updateUnitHumanMember.mockResolvedValue(
      makeHumanMember({ membershipId: "row-edit" }),
    );

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-edit-row-edit"),
      ).toBeInTheDocument();
    });

    fireEvent.click(screen.getByTestId("unit-human-member-edit-row-edit"));

    const dialog = await screen.findByRole("dialog");
    // The role field is read-only on edit (PATCH only touches
    // expertise + notifications per #2409).
    expect(
      within(dialog).getByTestId("human-member-dialog-role-readonly"),
    ).toHaveTextContent("tech-lead");

    fireEvent.change(
      within(dialog).getByTestId("human-member-dialog-expertise"),
      { target: { value: "frontend" } },
    );

    fireEvent.click(
      within(dialog).getByTestId("human-member-dialog-submit"),
    );

    await waitFor(() => {
      expect(updateUnitHumanMember).toHaveBeenCalledWith(
        "engineering",
        OPERATOR_GUID,
        "tech-lead",
        expect.objectContaining({ expertise: ["frontend"] }),
      );
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Member updated" }),
    );
  });

  it("calls DELETE on confirm when removing a human member", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);
    listUnitHumanMembers.mockResolvedValue([
      makeHumanMember({
        membershipId: "row-delete",
        humanId: OPERATOR_GUID,
        role: "tech-lead",
      }),
    ]);
    removeUnitHumanMember.mockResolvedValue(undefined);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByTestId("unit-human-member-remove-row-delete"),
      ).toBeInTheDocument();
    });

    fireEvent.click(
      screen.getByTestId("unit-human-member-remove-row-delete"),
    );

    const dialog = await screen.findByRole("dialog");
    expect(
      within(dialog).getByText(/Remove member from unit/i),
    ).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: /^remove$/i }));

    await waitFor(() => {
      expect(removeUnitHumanMember).toHaveBeenCalledWith(
        "engineering",
        OPERATOR_GUID,
        "tech-lead",
      );
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Member removed" }),
    );
  });

  it("pre-populates the edit MembershipDialog from the existing membership", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({
        agentAddress: "ada",
        model: "claude-opus-4-7",
        specialty: "reviewer",
        enabled: false,
        executionMode: "OnDemand",
      }),
    ]);

    const updated = makeMembership({
      agentAddress: "ada",
      model: "claude-sonnet-4-6",
      specialty: "reviewer",
      enabled: false,
      executionMode: "OnDemand",
    });
    upsertUnitMembership.mockResolvedValue(updated);

    renderMembersTab("engineering");

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("button", { name: /Edit Ada/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Edit membership/i)).toBeInTheDocument();
    expect(
      within(dialog).getByTestId("membership-dialog-agent-header"),
    ).toHaveTextContent("Ada");
    expect(
      (within(dialog).getByLabelText(/^Model$/i) as HTMLSelectElement).value,
    ).toBe("claude-opus-4-7");
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
      target: { value: "claude-sonnet-4-6" },
    });
    fireEvent.click(within(dialog).getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      expect(upsertUnitMembership).toHaveBeenCalledWith(
        "engineering",
        "ada",
        expect.objectContaining({ model: "claude-sonnet-4-6" }),
      );
    });
    expect(toastMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Membership updated" }),
    );
  });

  it("does not call DELETE when the user cancels the confirm dialog", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({ agentAddress: "ada" }),
    ]);

    renderMembersTab("engineering");

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

  it("calls DELETE on confirm and removes the agent row", async () => {
    const ada = makeAgent({ name: "ada", displayName: "Ada" });
    listAgents.mockResolvedValue([ada]);
    listUnitMemberships.mockResolvedValue([
      makeMembership({ agentAddress: "ada" }),
    ]);
    deleteUnitMembership.mockResolvedValue(undefined);

    renderMembersTab("engineering");

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
});
