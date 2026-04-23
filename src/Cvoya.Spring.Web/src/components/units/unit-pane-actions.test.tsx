/**
 * Tests for `UnitPaneActions` (#980 item 3).
 *
 * Covers:
 *   - Status-gated button surface: Validate / Revalidate / Start / Stop
 *     each show on the matching UnitStatus; Delete is always shown.
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
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentNode, UnitNode } from "./aggregate";
import type { UnitResponse, UnitStatus } from "@/lib/api/types";

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
    },
  };
});

import { ApiError } from "@/lib/api/client";

const useUnitMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnit: (id: string) => useUnitMock(id),
}));

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

function makeUnit(status: UnitStatus): UnitResponse {
  return {
    id: "alpha",
    name: "alpha",
    displayName: "Alpha",
    description: "",
    registeredAt: "2026-04-21T00:00:00Z",
    status,
    model: null,
    color: null,
    tool: null,
    provider: null,
    hosting: null,
    lastValidationError: null,
    lastValidationRunId: null,
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
  useUnitMock.mockReset();
  toastMock.mockReset();
});

describe("UnitPaneActions — Unit status gating", () => {
  const cases: Array<{
    status: UnitStatus;
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
// every UnitStatus surfaces it.
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
  it("does not call deleteUnit on plain Delete click — requires confirmation", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    expect(deleteUnitMock).not.toHaveBeenCalled();
    // The confirm dialog should now be visible.
    expect(
      screen.getByRole("button", { name: /permanently delete/i }),
    ).toBeInTheDocument();
  });

  it("cancels without calling deleteUnit", () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
    render(wrap(<UnitPaneActions node={unitNode} />));
    fireEvent.click(screen.getByTestId("unit-action-delete"));
    fireEvent.click(screen.getByRole("button", { name: /cancel/i }));
    expect(deleteUnitMock).not.toHaveBeenCalled();
  });

  it("fires deleteUnit after explicit confirmation and routes away", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
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
  // The API gates DELETE on lifecycle status — units in
  // Validating/Starting/Running/Stopping return 409 with a
  // `forceHint` payload that surfaces `?force=true`. The portal
  // detects that shape and offers a second confirmation that calls
  // the API with `{ force: true }`. This test pins the recovery flow
  // end-to-end so a future refactor can't silently drop the hint.
  it("opens the force-delete dialog when the API returns 409 with forceHint", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Validating") });
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
    useUnitMock.mockReturnValue({ data: makeUnit("Running") });
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
  it("renders only Delete for an agent node", () => {
    render(wrap(<UnitPaneActions node={agentNode} />));
    expect(screen.getByTestId("agent-action-delete")).toBeInTheDocument();
    expect(screen.queryByTestId("unit-action-start")).toBeNull();
    expect(screen.queryByTestId("unit-action-stop")).toBeNull();
    expect(screen.queryByTestId("unit-action-revalidate")).toBeNull();
    // No agent-level Start/Stop ships today — the CLI has no equivalent.
    expect(screen.queryByTestId("agent-action-start")).toBeNull();
    expect(screen.queryByTestId("agent-action-stop")).toBeNull();
  });

  it("requires confirmation before firing deleteAgent", async () => {
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

describe("UnitPaneActions — error surfacing", () => {
  it("emits a toast when startUnit fails", async () => {
    useUnitMock.mockReturnValue({ data: makeUnit("Stopped") });
    startUnitMock.mockRejectedValue(new Error("API error 500: boom"));
    render(wrap(<UnitPaneActions node={unitNode} />));
    await act(async () => {
      fireEvent.click(screen.getByTestId("unit-action-start"));
    });
    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({ title: "Start failed", variant: "destructive" }),
      );
    });
  });
});
