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

import type {
  AgentResponse,
  InstalledModelProviderResponse,
  UnitMembershipResponse,
} from "@/lib/api/types";

interface AgentCreateDialogMockProps {
  unitId: string;
  unitDisplayName: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const agentCreateDialogMock = vi.hoisted(() => vi.fn());

// Mock the API module: only the calls the tab actually makes need to be
// defined. Anything else left undefined would throw if accidentally called.
const listUnitMemberships =
  vi.fn<(unitId: string) => Promise<UnitMembershipResponse[]>>();
const listAgents = vi.fn<() => Promise<AgentResponse[]>>();
const upsertUnitMembership = vi.fn();
const deleteUnitMembership = vi.fn();
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

import { AgentsTab } from "./agents-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function renderAgentsTab(
  unitId: string,
  unitDisplayName = "Engineering",
) {
  return render(
    <Wrapper>
      <AgentsTab unitId={unitId} unitDisplayName={unitDisplayName} />
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
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto" as const,
    createdAt: now,
    updatedAt: now,
    isPrimary: false,
    ...overrides,
  };
  // #1060: `member` is the canonical scheme-prefixed address; default
  // it from `agentAddress` so the fixture stays terse but per-test
  // overrides still win.
  return {
    ...base,
    member: overrides.member ?? `agent://${base.agentAddress}`,
  };
}

// ADR-0038: minimal tenant-installed-provider fixture modelled after
// `InstalledModelProviderResponse`. Only the fields the dialog reads
// (`id`, `displayName`, `models`, `defaultModel`) are populated; the
// rest default to whatever the test runtime tolerates so the contract
// schema can grow without updating every test.
const DEFAULT_PROVIDERS = [
  {
    id: "anthropic",
    displayName: "Anthropic",
    models: [
      "claude-sonnet-4-6",
      "claude-opus-4-7",
    ],
    defaultModel: "claude-sonnet-4-6",
  },
  {
    id: "openai",
    displayName: "OpenAI",
    models: ["gpt-4o", "gpt-4o-mini"],
    defaultModel: "gpt-4o",
  },
] as unknown as InstalledModelProviderResponse[];

describe("AgentsTab", () => {
  beforeEach(() => {
    listUnitMemberships.mockReset();
    listAgents.mockReset();
    upsertUnitMembership.mockReset();
    deleteUnitMembership.mockReset();
    listModelProviders.mockReset();
    toastMock.mockReset();
    agentCreateDialogMock.mockReset();
    // Every edit-dialog test needs the runtimes list available; the
    // couple of tests that never open the dialog also benefit from a
    // stable default so no path triggers an unmocked call.
    listModelProviders.mockResolvedValue(DEFAULT_PROVIDERS);
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("shows the empty state and 'Add agent' button when no memberships exist", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([makeAgent({ name: "hopper" })]);

    renderAgentsTab("engineering");

    await waitFor(() => {
      expect(
        screen.getByText(/No agents assigned to this unit yet/i),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByRole("button", { name: /add agent/i }),
    ).toBeEnabled();
  });

  it("opens AgentCreateDialog with the current unit and refreshes memberships on close", async () => {
    listUnitMemberships.mockResolvedValue([]);
    listAgents.mockResolvedValue([]);

    renderAgentsTab("engineering", "Engineering Team");

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
    expect(agentCreateDialogMock).toHaveBeenCalledWith(
      expect.objectContaining({
        unitId: "engineering",
        unitDisplayName: "Engineering Team",
        open: true,
        onOpenChange: expect.any(Function),
      }),
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

  it("lists memberships with display names and per-membership config", async () => {
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

    renderAgentsTab("engineering");

    await waitFor(() => {
      expect(screen.getByText("Ada")).toBeInTheDocument();
    });
    expect(screen.getByText(/reviewer/i)).toBeInTheDocument();
    expect(screen.getByText(/claude-sonnet-4-6/)).toBeInTheDocument();
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

    renderAgentsTab("engineering");

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

    renderAgentsTab("engineering");

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

    renderAgentsTab("engineering");

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
