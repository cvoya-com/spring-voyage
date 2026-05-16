/**
 * Tests for `UnitPaneActions` (#980 item 3).
 *
 * Covers:
 *   - Status-gated button surface: Validate / Revalidate / Start / Stop
 *     each show on the matching LifecycleStatus; Delete is always shown.
 *   - Delete requires confirmation and only fires the mutation when the
 *     user explicitly confirms — Cancel dismisses without a POST.
 *   - Successful mutations invalidate the relevant query-key slices so
 *     the status badge and tree re-render.
 *   - Agent lifecycle only surfaces Delete today (Start/Stop have no
 *     CLI equivalent).
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "./aggregate";
import type { AgentDetailResponse, LifecycleStatus, UnitResponse } from "@/lib/api/types";

const routerReplaceMock = vi.fn();
const routerPushMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({
    replace: routerReplaceMock,
    push: routerPushMock,
  }),
}));

const startUnitMock = vi.fn();
const stopUnitMock = vi.fn();
const revalidateUnitMock = vi.fn();
const deleteUnitMock = vi.fn();
const deleteAgentMock = vi.fn();
const startAgentMock = vi.fn();
const stopAgentMock = vi.fn();
const revalidateAgentMock = vi.fn();

// Re-export the real ApiError so the production code's `instanceof
// ApiError` check inside the unit-pane-actions component matches the
// instances we throw from the test mocks. Mocking ApiError out would
// break the 409 forceHint detection used by the recovery flow.
vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>(
    "@/lib/api/client",
  );
  return {
    ...actual,
    api: {
      startUnit: (id: string) => startUnitMock(id),
      stopUnit: (id: string) => stopUnitMock(id),
      revalidateUnit: (id: string) => revalidateUnitMock(id),
      deleteUnit: (id: string, options?: { force?: boolean }) =>
        deleteUnitMock(id, options),
      deleteAgent: (id: string) => deleteAgentMock(id),
      startAgent: (id: string) => startAgentMock(id),
      stopAgent: (id: string) => stopAgentMock(id),
      revalidateAgent: (id: string) => revalidateAgentMock(id),
    },
  };
});

import { ApiError } from "@/lib/api/client";

const useUnitMock = vi.fn();
const useAgentMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnit: (id: string) => useUnitMock(id),
  useAgent: (id: string) => useAgentMock(id),
}));

function makeAgent(status: LifecycleStatus | null): AgentDetailResponse {
  return {
    agent: {
      id: "ada",
      name: "ada",
      displayName: "Ada",
      description: "",
      role: null,
      registeredAt: "2026-04-21T00:00:00Z",
      model: null,
      specialty: null,
      enabled: true,
      executionMode: "Auto",
      parentUnit: null,
      lifecycleStatus: status,
    },
    status: null,
    deployment: null,
  } as AgentDetailResponse;
}

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { UnitPaneActions } from "./unit-pane-actions";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

function makeUnit(status: LifecycleStatus): UnitResponse {
  return {
    id: "alpha",
    name: "alpha",
    displayName: "Alpha",
    description: "",
    registeredAt: "2026-04-21T00:00:00Z",
    status,
    model: null,
    color: null,
    hosting: null,
    lastValidationError: null,
    lastValidationRunId: null,
    enabled: true,
  } as UnitResponse;
}

const unitNode: UnitNode = {
  kind: "Unit",
  id: "alpha",
  name: "Alpha",
  status: "running",
};

const agentNode: AgentNode = {
  kind: "Agent",
  id: "ada",
  name: "Ada",
  status: "running",
};

beforeEach(() => {
  routerReplaceMock.mockReset();
  routerPushMock.mockReset();
  startUnitMock.mockReset();
  stopUnitMock.mockReset();
  revalidateUnitMock.mockReset();
  deleteUnitMock.mockReset();
  deleteAgentMock.mockReset();
  startAgentMock.mockReset();
  stopAgentMock.mockReset();
  revalidateAgentMock.mockReset();
  useUnitMock.mockReset();
  useAgentMock.mockReset();
  // Default to "no data yet" so tests that don't care about the agent
  // detail query (engagement / sub-unit navigation / agent-only chrome)
  // don't crash with `agentQuery is undefined`. Tests that need a
  // concrete status override this per-it.
  useAgentMock.mockReturnValue({ data: undefined });
  useUnitMock.mockReturnValue({ data: undefined });
  toastMock.mockReset();
});

describe("UnitPaneActions — Unit status gating", () => {
  const cases: Array<{
    status: LifecycleStatus;
    visible: string[];
    hidden: string[];
  }> = [
    {
      status: "Draft",
      visible: ["unit-action-validate", "unit-action-delete"],
      hidden: [
        "unit-action-revalidate",
        "unit-action-start",
        "unit-action-stop",
      ],
    },
    {
      status: "Stopped",
      visible: [
        "unit-action-revalidate",
        "unit-action-start",
        "unit-action-delete",
      ],
      hidden: ["unit-action-validate", "unit-action-stop"],
    },
    {
      status: "Running",
      visible: ["unit-action-stop", "unit-action-delete"],
      hidden: [
        "unit-action-validate",
        "unit-action-start",
        "unit-action-revalidate",
      ],
    },
    {
      status: "Error",
      visible: ["unit-action-revalidate", "unit-action-delete"],
      hidden: [
        "unit-action-validate",
        "unit-action-start",
        "unit-action-stop",
      ],
    },
    {
      status: "Validating",
      visible: ["unit-action-delete"],
      hidden: [
        "unit-action-validate",
        "unit-action-revalidate",
        "unit-action-start",
        "unit-action-stop",
      ],
    },
  ];

  for (const c of cases) {
    it(`renders the expected buttons for status="${c.status}"`, () => {
      useUnitMock.mockReturnValue({ data: makeUnit(c.status) });
      render(wrap(<UnitPaneActions node={unitNode} />));
      for (const id of c.visible) {
        expect(screen.getByTestId(id)).toBeInTheDocument();
      }
      for (const id of c.hidden) {
        expect(screen.queryByTestId(id)).toBeNull();
      }
      // #1150: Create sub-unit is always available — every unit can be
      // a parent regardless of lifecycle status.
      expect(
        screen.getByTestId("unit-action-create-subunit"),
      ).toBeInTheDocument();
    });
  }
});

// #1150: the "Create sub-unit" affordance routes to the create-unit
// wizard with the current unit pre-selected as the parent. The
// wizard reads the `parent` query param and threads `parentUnitIds`
// onto the create-unit API call. The button is unconditional — see
// the status-gating loop above for the cross-status assertion that
// every LifecycleStatus surfaces it.
describe("UnitPaneActions — Engagement (#1463 / #1464)", () => {
  it("navigates to /engagement/mine pre-selecting the unit", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-engagement"));
    });
    expect(routerPushMock).toHaveBeenCalledWith(
      "/engagement/mine?unit=" + encodeURIComponent("alpha"),
    );
  });

  it("navigates to /engagement/mine pre-selecting the agent", async () => {
    render(wrap(<UnitPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-engagement"));
    });
    expect(routerPushMock).toHaveBeenCalledWith(
      "/engagement/mine?agent=" + encodeURIComponent("ada"),
    );
  });

  it("uses the label 'Engagement' (not 'Start engagement') on both nodes", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    const { rerender } = render(wrap(<UnitPaneActions node={unitNode} />));
    expect(screen.getByTestId("unit-action-engagement")).toHaveTextContent(
      /^\s*Engagement\s*$/,
    );
    rerender(wrap(<UnitPaneActions node={agentNode} />));
    expect(screen.getByTestId("agent-action-engagement")).toHaveTextContent(
      /^\s*Engagement\s*$/,
    );
  });
});

describe("UnitPaneActions — Create sub-unit (#1150)", () => {
  it("navigates to /units/create with the parent query param", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-create-subunit"));
    });
    expect(routerPushMock).toHaveBeenCalledWith(
      "/units/create?parent=alpha",
    );
  });

  it("URL-encodes parent ids so address-shaped names survive the round-trip", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    const nestedNode: UnitNode = {
      kind: "Unit",
      id: "engineering/team alpha",
      name: "engineering/team alpha",
      status: "stopped",
    };
    render(wrap(<UnitPaneActions node={nestedNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-create-subunit"));
    });
    expect(routerPushMock).toHaveBeenCalledWith(
      "/units/create?parent=engineering%2Fteam%20alpha",
    );
  });

  it("is not rendered for agent nodes", () => {
    render(wrap(<UnitPaneActions node={agentNode} />));
    expect(
      screen.queryByTestId("unit-action-create-subunit"),
    ).toBeNull();
  });
});

describe("UnitPaneActions — Start / Stop / Revalidate / Validate", () => {
  it("fires startUnit when Start is clicked", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    startUnitMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-start"));
    });
    await waitFor(() => {
      expect(startUnitMock).toHaveBeenCalledWith("alpha");
    });
  });

  it("fires stopUnit when Stop is clicked", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
    stopUnitMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-stop"));
    });
    await waitFor(() => {
      expect(stopUnitMock).toHaveBeenCalledWith("alpha");
    });
  });

  it("fires revalidateUnit when Revalidate is clicked", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Error") });
    revalidateUnitMock.mockResolvedValue(undefined);
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-revalidate"));
    });
    await waitFor(() => {
      expect(revalidateUnitMock).toHaveBeenCalledWith("alpha");
    });
  });

  it("fires revalidateUnit when Validate is clicked on a Draft unit", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Draft") });
    revalidateUnitMock.mockResolvedValue(undefined);
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-validate"));
    });
    await waitFor(() => {
      expect(revalidateUnitMock).toHaveBeenCalledWith("alpha");
    });
  });
});

describe("UnitPaneActions — Delete confirmation flow", () => {
  // Use "Stopped" — a status where delete is allowed (#1019 gate is off).
  it("does not call deleteUnit on plain Delete click — requires confirmation", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    expect(deleteUnitMock).not.toHaveBeenCalled();
    // The confirm dialog should now be visible.
    expect(
      screen.getByRole("button", { name: /permanently delete/i }),
    ).toBeInTheDocument();
  });

  it("cancels without calling deleteUnit", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(deleteUnitMock).not.toHaveBeenCalled();
  });

  it("fires deleteUnit after explicit confirmation and routes away", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    deleteUnitMock.mockResolvedValue(undefined);
    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    await act(async () => {
      fireEvent.click(
        screen.getByRole("button", { name: /permanently delete/i }),
      );
    });
    await waitFor(() => {
      expect(deleteUnitMock).toHaveBeenCalledWith("alpha", undefined);
    });
    await waitFor(() => {
      expect(routerReplaceMock).toHaveBeenCalledWith("/units");
    });
  });
});

describe("UnitPaneActions — Force delete recovery (#1137)", () => {
  // The API can return 409 + forceHint even from a unit in a "deletable"
  // state when there's a race (the unit started between the button click
  // and the confirm). Use "Stopped" so the delete button is enabled
  // (#1019 gate) and the confirm flow can proceed.
  it("opens the force-delete dialog when the API returns 409 with forceHint", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    deleteUnitMock.mockRejectedValueOnce(
      new ApiError(409, "Conflict", {
        forceHint: "Pass ?force=true to bypass the lifecycle gate.",
      }),
    );
    deleteUnitMock.mockResolvedValueOnce(undefined);

    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    await act(async () => {
      fireEvent.click(
        screen.getByRole("button", { name: /permanently delete/i }),
      );
    });

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: /force delete/i }),
      ).toBeInTheDocument();
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /force delete/i }));
    });

    await waitFor(() => {
      expect(deleteUnitMock).toHaveBeenLastCalledWith("alpha", { force: true });
    });
    await waitFor(() => {
      expect(routerReplaceMock).toHaveBeenCalledWith("/units");
    });
  });

  it("surfaces a normal error toast when the 409 has no forceHint", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    deleteUnitMock.mockRejectedValueOnce(
      new ApiError(409, "Conflict", { detail: "no hint here" }),
    );

    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    await act(async () => {
      fireEvent.click(
        screen.getByRole("button", { name: /permanently delete/i }),
      );
    });

    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Delete failed",
          variant: "destructive",
        }),
      );
    });
    expect(
      screen.queryByRole("button", { name: /force delete/i }),
    ).toBeNull();
  });
});

describe("UnitPaneActions — Agent", () => {
  it("renders Engagement + Delete for an agent node with no lifecycle data", () => {
    useAgentMock.mockReturnValue({ data: undefined });
    render(wrap(<UnitPaneActions node={agentNode} />));
    expect(screen.getByTestId("agent-action-delete")).toBeInTheDocument();
    // #1463/#1464: agent panes also surface the engagement entry-point.
    expect(
      screen.getByTestId("agent-action-engagement"),
    ).toBeInTheDocument();
    expect(screen.queryByTestId("unit-action-start")).toBeNull();
    expect(screen.queryByTestId("unit-action-stop")).toBeNull();
    expect(screen.queryByTestId("unit-action-revalidate")).toBeNull();
    // Without a known status no lifecycle verbs render — same as the
    // unit-side fallback.
    expect(screen.queryByTestId("agent-action-start")).toBeNull();
    expect(screen.queryByTestId("agent-action-stop")).toBeNull();
    expect(screen.queryByTestId("agent-action-validate")).toBeNull();
    expect(screen.queryByTestId("agent-action-revalidate")).toBeNull();
  });

  it("requires confirmation before firing deleteAgent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Stopped") });
    deleteAgentMock.mockResolvedValue(undefined);
    render(wrap(<UnitPaneActions node={agentNode} />));
    fireEvent.click(screen.getByTestId("agent-action-delete"));
    expect(deleteAgentMock).not.toHaveBeenCalled();

    await act(async () => {
      fireEvent.click(
        screen.getByRole("button", { name: /permanently delete/i }),
      );
    });
    await waitFor(() => {
      expect(deleteAgentMock).toHaveBeenCalledWith("ada");
    });
    await waitFor(() => {
      expect(routerReplaceMock).toHaveBeenCalledWith("/units");
    });
  });
});

// #2372: agent surface now mirrors the unit verbs — Run / Stop /
// Revalidate / Validate, status-gated against the live LifecycleStatus
// read from `useAgent(id)`. Status-gating + dispatch coverage follows
// the unit-side test layout so future status-mapping changes have one
// place to update both sides.
describe("UnitPaneActions — Agent status gating (#2372)", () => {
  const cases: Array<{
    status: LifecycleStatus;
    visible: string[];
    hidden: string[];
  }> = [
    {
      status: "Draft",
      visible: ["agent-action-validate", "agent-action-delete"],
      hidden: [
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
    {
      status: "Stopped",
      visible: [
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-delete",
      ],
      hidden: ["agent-action-validate", "agent-action-stop"],
    },
    {
      status: "Running",
      visible: ["agent-action-stop", "agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-start",
        "agent-action-revalidate",
      ],
    },
    {
      status: "Error",
      visible: ["agent-action-revalidate", "agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
    {
      status: "Validating",
      visible: ["agent-action-delete"],
      hidden: [
        "agent-action-validate",
        "agent-action-revalidate",
        "agent-action-start",
        "agent-action-stop",
      ],
    },
  ];

  for (const c of cases) {
    it(`renders the expected buttons for status="${c.status}"`, () => {
      useAgentMock.mockReturnValue({ data: makeAgent(c.status) });
      render(wrap(<UnitPaneActions node={agentNode} />));
      for (const id of c.visible) {
        expect(screen.getByTestId(id)).toBeInTheDocument();
      }
      for (const id of c.hidden) {
        expect(screen.queryByTestId(id)).toBeNull();
      }
    });
  }
});

describe("UnitPaneActions — Agent Start / Stop / Revalidate dispatch (#2372)", () => {
  it("fires startAgent when Run is clicked on a Stopped agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Stopped") });
    startAgentMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-start"));
    });
    await waitFor(() => {
      expect(startAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires stopAgent when Stop is clicked on a Running agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Running") });
    stopAgentMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-stop"));
    });
    await waitFor(() => {
      expect(stopAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires revalidateAgent when Revalidate is clicked on an Error agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Error") });
    revalidateAgentMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-revalidate"));
    });
    await waitFor(() => {
      expect(revalidateAgentMock).toHaveBeenCalledWith("ada");
    });
  });

  it("fires revalidateAgent when Validate is clicked on a Draft agent", async () => {
    useAgentMock.mockReturnValue({ data: makeAgent("Draft") });
    revalidateAgentMock.mockResolvedValue({});
    render(wrap(<UnitPaneActions node={agentNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("agent-action-validate"));
    });
    await waitFor(() => {
      expect(revalidateAgentMock).toHaveBeenCalledWith("ada");
    });
  });
});

describe("UnitPaneActions — error surfacing", () => {
  it("emits a translated toast when startUnit returns ProblemDetails", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    startUnitMock.mockRejectedValue(
      new ApiError(409, "Conflict", {
        type: "https://cvoya.com/problems/lifecycle-conflict",
        title: "Conflict",
        status: 409,
        detail: "Unit 'alpha' is Running; start is only allowed from Stopped.",
        code: "LifecycleConflict",
        action: "start",
        currentStatus: "Running",
        traceId: "00-lifecycle",
      }),
    );
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-start"));
    });
    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Start failed",
          variant: "destructive",
          description:
            "Can't start this unit while it's `Running`. Wait for the current operation to finish, then retry.",
        }),
      );
    });
    expect(JSON.stringify(toastMock.mock.calls)).not.toContain("API error 409");
    expect(JSON.stringify(toastMock.mock.calls)).not.toContain(
      "LifecycleConflict:",
    );
  });
});

// #1019: Delete must be disabled (not clickable) when the unit is in a
// lifecycle state where the API would reject the request. The button is
// still rendered so operators know it exists — it just isn't actionable
// until the unit reaches a deletable state (Draft, Stopped, or Error).
describe("UnitPaneActions — delete gating (#1019)", () => {
  const blockingStatuses: LifecycleStatus[] = [
    "Running",
    "Starting",
    "Stopping",
    "Validating",
  ];
  const allowedStatuses: LifecycleStatus[] = ["Draft", "Stopped", "Error"];

  for (const status of blockingStatuses) {
    it(`disables the Delete button when status is "${status}"`, () => {
      useUnitMock.mockReturnValue({ data: makeUnit(status) });
      render(wrap(<UnitPaneActions node={unitNode} />));
      const btn = screen.getByTestId("unit-action-delete") as HTMLButtonElement;
      expect(btn.disabled).toBe(true);
      expect(btn.getAttribute("data-delete-blocked")).toBe("true");
    });
  }

  for (const status of allowedStatuses) {
    it(`enables the Delete button when status is "${status}"`, () => {
      useUnitMock.mockReturnValue({ data: makeUnit(status) });
      render(wrap(<UnitPaneActions node={unitNode} />));
      const btn = screen.getByTestId("unit-action-delete") as HTMLButtonElement;
      expect(btn.disabled).toBe(false);
      expect(btn.getAttribute("data-delete-blocked")).toBeNull();
    });
  }

  it("does not open the confirm dialog when the Delete button is blocked (Running)", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    const btn = screen.getByTestId("unit-action-delete");
    fireEvent.click(btn);
    // The confirm dialog should NOT appear — the click guard is active.
    expect(
      screen.queryByRole("button", { name: /permanently delete/i }),
    ).toBeNull();
    expect(deleteUnitMock).not.toHaveBeenCalled();
  });
});

// #1145: soft-timeout advisory for units stuck in Starting / Stopping.
// The timer fires after EXPLORER_STUCK_THRESHOLD_MS (90 s) of continuous
// time in the transient state. Tests use vi.useFakeTimers() to advance
// the clock without waiting.
describe("UnitPaneActions — stuck-transient advisory (#1145)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("does not render the advisory before the threshold elapses (Starting)", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Starting") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    // Advance 30 s — well below the 90 s threshold.
    act(() => { vi.advanceTimersByTime(30_000); });
    expect(screen.queryByTestId("unit-stuck-advisory")).toBeNull();
  });

  it("renders the advisory after the threshold elapses for Starting", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Starting") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    act(() => { vi.advanceTimersByTime(90_000); });
    expect(screen.getByTestId("unit-stuck-advisory")).toBeInTheDocument();
    expect(screen.getByTestId("unit-stuck-force-delete")).toBeInTheDocument();
    expect(screen.getByTestId("unit-stuck-dismiss")).toBeInTheDocument();
  });

  it("renders the advisory after the threshold elapses for Stopping", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopping") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    act(() => { vi.advanceTimersByTime(90_000); });
    expect(screen.getByTestId("unit-stuck-advisory")).toBeInTheDocument();
  });

  it("does not render the advisory for non-transient statuses", () => {
    for (const status of ["Running", "Stopped", "Error", "Draft", "Validating"] as const) {
      useUnitMock.mockReturnValue({ data: makeUnit(status as LifecycleStatus) });
      const { unmount } = render(wrap(<UnitPaneActions node={unitNode} />));
      act(() => { vi.advanceTimersByTime(90_000); });
      expect(screen.queryByTestId("unit-stuck-advisory")).toBeNull();
      unmount();
    }
  });

  it("Dismiss hides the advisory for the current status", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Starting") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    act(() => { vi.advanceTimersByTime(90_000); });
    expect(screen.getByTestId("unit-stuck-advisory")).toBeInTheDocument();
    fireEvent.click(screen.getByTestId("unit-stuck-dismiss"));
    expect(screen.queryByTestId("unit-stuck-advisory")).toBeNull();
  });

  it("Force delete button opens the force-delete confirm dialog", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Starting") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    act(() => { vi.advanceTimersByTime(90_000); });
    fireEvent.click(screen.getByTestId("unit-stuck-force-delete"));
    // The force-delete confirmation dialog title should appear.
    expect(
      screen.getByRole("heading", { name: /force-delete unit/i }),
    ).toBeInTheDocument();
  });
});
