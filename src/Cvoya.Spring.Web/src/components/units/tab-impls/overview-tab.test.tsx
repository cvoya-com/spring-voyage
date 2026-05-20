import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  AgentNode,
  HumanNode,
  TenantNode,
  UnitNode,
} from "../aggregate";

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: { href: string; children: ReactNode } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

vi.mock("../unit-overview-expertise-card", () => ({
  UnitOverviewExpertiseCard: ({ unitId }: { unitId: string }) => (
    <div data-testid="expertise-card-stub" data-unit-id={unitId} />
  ),
}));

vi.mock("@/components/agents/tab-impls/lifecycle-panel", () => ({
  LifecyclePanel: ({ agentId }: { agentId: string }) => (
    <div data-testid="legacy-lifecycle" data-agent-id={agentId} />
  ),
}));

vi.mock("@/components/cards/unit-card", () => ({
  UnitCard: ({
    unit,
  }: {
    unit: { name: string; displayName: string };
  }) => (
    <div
      data-testid={`unit-card-${unit.name}`}
      data-display-name={unit.displayName}
    />
  ),
}));

// Hook mocks — the canonical body calls them unconditionally inside
// the kind-guarded sub-components, so we provide a default-empty
// shape and let individual tests opt-in to populated data.
const useUnitCostTimeseriesMock = vi.fn();
const useUnitMock = vi.fn();
const useUnitExecutionMock = vi.fn();
const useAgentCostMock = vi.fn();
const useUnitIssuesMock = vi.fn();
const useAgentIssuesMock = vi.fn();
const useHumanMock = vi.fn();
const useCurrentUserMock = vi.fn();
vi.mock("@/lib/api/queries", () => ({
  useUnitCostTimeseries: (id: string, window: string, bucket: string) =>
    useUnitCostTimeseriesMock(id, window, bucket),
  useUnit: (id: string, opts?: unknown) => useUnitMock(id, opts),
  useUnitExecution: (id: string) => useUnitExecutionMock(id),
  useAgentCost: (id: string) => useAgentCostMock(id),
  useUnitIssues: (id: string, opts?: unknown) => useUnitIssuesMock(id, opts),
  useAgentIssues: (id: string) => useAgentIssuesMock(id),
  useHuman: (id: string, opts?: unknown) => useHumanMock(id, opts),
  useCurrentUser: () => useCurrentUserMock(),
  useModelProviders: () => ({ data: [], isPending: false, isError: false }),
}));

// #2387: tenant-tree invalidation moved out of OverviewTab into the
// activity-stream SSE handler (`queryKeysAffectedBySource`). The
// behaviour is covered there by `use-activity-stream.test.tsx`; the
// Overview tab no longer touches the query client directly.

// `<ValidationPanel>` is rendered for `Error` units; stub it out so
// the Overview tests don't need to thread the panel's full mutation /
// query-client wiring through every assertion.
vi.mock("../detail/validation-panel", () => ({
  default: ({
    unit,
  }: {
    unit: { lastValidationError?: { code?: string } | null };
  }) => (
    <div
      data-testid="validation-panel-stub"
      data-validation-code={unit.lastValidationError?.code ?? ""}
    />
  ),
}));

import { OverviewTab } from "./overview-tab";

const emptyTimeseries = { data: null, isLoading: false };
const noUnit = { data: null, isLoading: false };
const noExecution = { data: null, isLoading: false };
const noIssues = { data: null, isPending: false, isError: false };

beforeEach(() => {
  useUnitCostTimeseriesMock.mockReset();
  useUnitMock.mockReset();
  useUnitExecutionMock.mockReset();
  useAgentCostMock.mockReset();
  useUnitIssuesMock.mockReset();
  useAgentIssuesMock.mockReset();
  useHumanMock.mockReset();
  useCurrentUserMock.mockReset();
  useUnitCostTimeseriesMock.mockReturnValue(emptyTimeseries);
  useUnitMock.mockReturnValue(noUnit);
  useUnitExecutionMock.mockReturnValue(noExecution);
  useUnitIssuesMock.mockReturnValue(noIssues);
  useAgentIssuesMock.mockReturnValue(noIssues);
  useHumanMock.mockReturnValue({ data: null, isLoading: false });
  useCurrentUserMock.mockReturnValue({ data: null });
});

describe("OverviewTab (Tenant subject)", () => {
  it("renders the empty state when tenant has no units", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [],
    };
    render(<OverviewTab kind="Tenant" node={node} />);
    expect(
      screen.getByTestId("tab-tenant-overview-empty"),
    ).toBeInTheDocument();
  });

  it("renders a UnitCard for each top-level unit", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [
        {
          kind: "Unit",
          id: "engineering",
          name: "Engineering",
          status: "running",
        },
        {
          kind: "Unit",
          id: "platform",
          name: "Platform",
          status: "paused",
        },
      ],
    };
    render(<OverviewTab kind="Tenant" node={node} />);
    expect(screen.getByTestId("unit-card-engineering")).toBeInTheDocument();
    expect(screen.getByTestId("unit-card-platform")).toBeInTheDocument();
  });

  it("does not mount unit- or agent-side hooks", () => {
    const node: TenantNode = {
      kind: "Tenant",
      id: "tenant",
      name: "Tenant",
      status: "running",
      children: [],
    };
    render(<OverviewTab kind="Tenant" node={node} />);
    expect(useUnitCostTimeseriesMock).not.toHaveBeenCalled();
    expect(useAgentCostMock).not.toHaveBeenCalled();
    expect(useUnitIssuesMock).not.toHaveBeenCalled();
    expect(useAgentIssuesMock).not.toHaveBeenCalled();
  });
});

describe("OverviewTab (Unit subject)", () => {
  it("renders subtree stat tiles rolled up from the node", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
      cost24h: 2.5,
      msgs24h: 42,
      children: [
        {
          kind: "Agent",
          id: "ada",
          name: "Ada",
          status: "running",
          cost24h: 1.25,
          msgs24h: 10,
        },
      ],
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(screen.getByTestId("tab-unit-overview")).toBeInTheDocument();
    // Messages tile shows the rolled-up total (42 + 10 = 52).
    expect(screen.getByText("52")).toBeInTheDocument();
    expect(screen.getByText("Cost (24h)")).toBeInTheDocument();
  });

  it("mounts the expertise card with the unit id (issue #936)", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(
      screen.getByTestId("expertise-card-stub").dataset.unitId,
    ).toBe("engineering");
  });

  it("shows the cost timeseries card with empty state when no data", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(
      screen.getByTestId("unit-cost-timeseries-card"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("unit-cost-timeseries-empty"),
    ).toBeInTheDocument();
  });

  it("renders the sparkline when timeseries data is present", () => {
    useUnitCostTimeseriesMock.mockReturnValue({
      data: {
        scope: "unit",
        id: "engineering",
        bucket: "1d",
        from: "2024-01-01T00:00:00Z",
        to: "2024-01-08T00:00:00Z",
        points: [
          { t: "2024-01-01T00:00:00Z", costUsd: 2.0 },
          { t: "2024-01-02T00:00:00Z", costUsd: 3.5 },
          { t: "2024-01-03T00:00:00Z", costUsd: 1.8 },
        ],
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(screen.getByTestId("unit-cost-sparkline")).toBeInTheDocument();
  });

  it("renders the cross-portal engagement link with the unit id (E2.3 #1415)", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    const link = screen.getByTestId("unit-overview-engagement-link");
    expect(link).toHaveAttribute(
      "href",
      "/engagement/mine?unit=engineering",
    );
    expect(link).toHaveTextContent("View engagements for this unit");
  });

  // #1665: when the live unit is in `Error`, the validation panel
  // surfaces the structured `lastValidationError` so the operator
  // sees *why* validation failed without leaving the Overview tab.
  it("renders the validation panel when the live unit is in Error", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "engineering",
        name: "engineering",
        displayName: "Engineering",
        status: "Error",
        lastValidationError: {
          step: "SchedulingWorkflow",
          code: "ConfigurationIncomplete",
          message:
            "No execution defaults are configured on this unit. Set a container image (and optionally a runtime) before validation can run.",
          details: { missing: "image,runtime" },
        },
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "error",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    const panel = screen.getByTestId("validation-panel-stub");
    expect(panel.dataset.validationCode).toBe("ConfigurationIncomplete");
  });

  // #1787: the Status tile must reflect the live unit endpoint, not
  // the cached tenant-tree's worst-status aggregate.
  it("#1787: shows live unit status in Status tile when unitQuery resolves", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "engineering",
        name: "engineering",
        displayName: "Engineering",
        status: "Stopped",
        lastValidationError: null,
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(screen.getByText("Status")).toBeInTheDocument();
    expect(screen.getByText("stopped")).toBeInTheDocument();
  });

  // #1787: while the unit is validating we poll every 3 s so the
  // Status tile updates without a manual refresh. Tenant-tree
  // invalidation on the Validating→terminal edge used to live here
  // too; it moved into the SSE handler under #2387, so the tab itself
  // is only responsible for the refetch cadence now.
  it("#1787: passes a refetchInterval that returns 3000 ms while validating", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "engineering",
        name: "engineering",
        displayName: "Engineering",
        status: "Validating",
        lastValidationError: null,
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "validating",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    const opts = useUnitMock.mock.calls.at(-1)?.[1] as
      | { refetchInterval?: unknown }
      | undefined;
    const refetchInterval = opts?.refetchInterval;
    expect(typeof refetchInterval).toBe("function");
    if (typeof refetchInterval !== "function") return;
    expect(
      refetchInterval({ state: { data: { status: "Validating" } } }),
    ).toBe(3000);
    expect(
      refetchInterval({ state: { data: { status: "Stopped" } } }),
    ).toBe(false);
  });

  it("does not render the validation panel for healthy units", () => {
    useUnitMock.mockReturnValue({
      data: {
        id: "engineering",
        name: "engineering",
        displayName: "Engineering",
        status: "Running",
        lastValidationError: null,
      },
      isLoading: false,
    });
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(
      screen.queryByTestId("validation-panel-stub"),
    ).not.toBeInTheDocument();
  });

  it("does not mount the Agent-side cost or lifecycle hooks", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(useAgentCostMock).not.toHaveBeenCalled();
    expect(useAgentIssuesMock).not.toHaveBeenCalled();
    // The agent-only LifecyclePanel must not mount for a Unit subject.
    expect(screen.queryByTestId("legacy-lifecycle")).not.toBeInTheDocument();
  });

  it("renders the description above the stat grid when desc is set", () => {
    const node: UnitNode = {
      kind: "Unit",
      id: "engineering",
      name: "Engineering",
      status: "running",
      desc: "Core platform engineering",
    };
    render(<OverviewTab kind="Unit" node={node} />);
    expect(
      screen.getByText("Core platform engineering"),
    ).toBeInTheDocument();
  });
});

describe("OverviewTab (Agent subject)", () => {
  const node: AgentNode = {
    kind: "Agent",
    id: "ada",
    name: "Ada",
    status: "running",
  };

  it("wires the lifecycle panel and a cost summary empty-state when no cost yet", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    render(<OverviewTab kind="Agent" node={node} />);
    expect(screen.getByTestId("legacy-lifecycle").dataset.agentId).toBe(
      "ada",
    );
    expect(
      screen.getByTestId("tab-agent-overview-cost-empty"),
    ).toBeInTheDocument();
  });

  it("renders totals when cost data is available", () => {
    useAgentCostMock.mockReturnValueOnce({
      data: {
        totalCost: 1.23,
        totalInputTokens: 100,
        totalOutputTokens: 50,
        recordCount: 4,
      },
    });
    render(<OverviewTab kind="Agent" node={node} />);
    expect(screen.getByText("100")).toBeInTheDocument();
  });

  it("renders the cross-portal engagement link with the agent id (E2.3 #1415)", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    render(<OverviewTab kind="Agent" node={node} />);
    const link = screen.getByTestId("agent-overview-engagement-link");
    expect(link).toHaveAttribute("href", "/engagement/mine?agent=ada");
    expect(link).toHaveTextContent("View engagements for this agent");
  });

  it("does not mount the Unit-side hooks (no 404 against an agent id)", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    render(<OverviewTab kind="Agent" node={node} />);
    expect(useUnitCostTimeseriesMock).not.toHaveBeenCalled();
    expect(useUnitMock).not.toHaveBeenCalled();
    expect(useUnitExecutionMock).not.toHaveBeenCalled();
    expect(useUnitIssuesMock).not.toHaveBeenCalled();
    // No validation panel and no expertise card on an Agent.
    expect(
      screen.queryByTestId("validation-panel-stub"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("expertise-card-stub"),
    ).not.toBeInTheDocument();
  });

  it("renders the description above the cards when desc is set", () => {
    useAgentCostMock.mockReturnValueOnce({ data: null });
    const withDesc: AgentNode = { ...node, desc: "Lead reviewer agent." };
    render(<OverviewTab kind="Agent" node={withDesc} />);
    expect(screen.getByText("Lead reviewer agent.")).toBeInTheDocument();
  });
});

describe("OverviewTab (Human subject — #2267)", () => {
  const SAMPLE_GUID = "11111111-1111-1111-1111-111111111111";
  const node: HumanNode = {
    kind: "Human",
    id: SAMPLE_GUID,
    name: "savas",
    status: "running",
  };

  it("renders the loading skeleton while the human query is in flight", () => {
    useHumanMock.mockReturnValue({ data: undefined, isLoading: true });
    render(<OverviewTab kind="Human" node={node} />);
    expect(
      screen.getByTestId("tab-human-overview-loading"),
    ).toBeInTheDocument();
  });

  it("renders the missing empty state when the entity is null (404)", () => {
    useHumanMock.mockReturnValue({ data: null, isLoading: false });
    render(<OverviewTab kind="Human" node={node} />);
    expect(
      screen.getByTestId("tab-human-overview-missing"),
    ).toBeInTheDocument();
  });

  it("#2531: queries the human-detail API with the bare guid when the node id carries the `human://` scheme", () => {
    // Explorer-tree human nodes carry the scheme-prefixed address
    // (`human://<no-dash-guid>`) the server emits. The human-detail
    // endpoint is keyed by a bare guid, so the scheme must be stripped
    // before the read — otherwise every human reached through the
    // Explorer 404s and renders "Human not found".
    const noDashGuid = "6860861c747347518757881972da962a";
    const schemeNode: HumanNode = {
      kind: "Human",
      id: `human://${noDashGuid}`,
      name: "Operator",
      status: "running",
    };
    useHumanMock.mockReturnValue({ data: null, isLoading: false });
    render(<OverviewTab kind="Human" node={schemeNode} />);
    expect(useHumanMock).toHaveBeenCalledWith(noDashGuid, undefined);
  });

  it("renders the profile card with display name, username, email, platformRole, and createdAt", () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas Parastatidis",
        email: "savas@example.com",
        platformRole: "Owner",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
    });
    render(<OverviewTab kind="Human" node={node} />);
    expect(
      screen.getByTestId("tab-human-overview-display-name"),
    ).toHaveTextContent("Savas Parastatidis");
    // Each of these values renders twice — once in the profile card's
    // Row and once in the facts grid Fact tile. Assert at-least-once
    // via `getAllByText` so the test stays robust to duplicate render.
    expect(
      screen.getAllByText("savas@example.com").length,
    ).toBeGreaterThan(0);
    expect(screen.getAllByText("Owner").length).toBeGreaterThan(0);
    expect(screen.getAllByText("2026-01-15").length).toBeGreaterThan(0);
  });

  it("shows the You hint when the current user matches the loaded human by Id (#2082 contract)", () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas",
        email: null,
        platformRole: "Operator",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
    });
    useCurrentUserMock.mockReturnValue({
      data: { id: SAMPLE_GUID, displayName: "Savas", userId: "savas" },
    });
    render(<OverviewTab kind="Human" node={node} />);
    expect(
      screen.getByTestId("tab-human-overview-you-hint"),
    ).toHaveTextContent(/you/i);
  });

  it("hides the You hint when the current user is a different human", () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "teammate",
        displayName: "Teammate",
        email: null,
        platformRole: "Operator",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
    });
    useCurrentUserMock.mockReturnValue({
      data: {
        id: "99999999-9999-9999-9999-999999999999",
        displayName: "Other",
        userId: "other",
      },
    });
    render(<OverviewTab kind="Human" node={node} />);
    expect(
      screen.queryByTestId("tab-human-overview-you-hint"),
    ).toBeNull();
  });

  it("renders the facts grid with the four canonical cells", () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas",
        email: "savas@example.com",
        platformRole: "Owner",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
    });
    render(<OverviewTab kind="Human" node={node} />);
    expect(screen.getByTestId("tab-human-overview-facts")).toBeInTheDocument();
    // Four <Fact> tiles → four "label" headings.
    expect(screen.getAllByText("Username").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Email").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Platform role").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Created").length).toBeGreaterThan(0);
  });

  it("does not mount unit- or agent-side hooks", () => {
    useHumanMock.mockReturnValue({
      data: {
        id: SAMPLE_GUID,
        username: "savas",
        displayName: "Savas",
        email: null,
        platformRole: "Operator",
        createdAt: "2026-01-15T10:00:00Z",
      },
      isLoading: false,
    });
    render(<OverviewTab kind="Human" node={node} />);
    expect(useUnitCostTimeseriesMock).not.toHaveBeenCalled();
    expect(useAgentCostMock).not.toHaveBeenCalled();
    expect(useUnitIssuesMock).not.toHaveBeenCalled();
    expect(useAgentIssuesMock).not.toHaveBeenCalled();
    expect(useUnitExecutionMock).not.toHaveBeenCalled();
    // Lifecycle panel must not render for a human subject.
    expect(screen.queryByTestId("legacy-lifecycle")).not.toBeInTheDocument();
    // Expertise card must not render either.
    expect(
      screen.queryByTestId("expertise-card-stub"),
    ).not.toBeInTheDocument();
  });
});
