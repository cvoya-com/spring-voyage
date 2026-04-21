// Agent detail page tabbed layout (#604). The spec covers the four
// behaviours the maintainer called out in the decision comment:
//
//   1. Each tab renders the cards we promised it would.
//   2. Runtime is the default open tab when no `?tab=` query is present.
//   3. `?tab=settings` on page load opens the Settings tab, and clicking
//      another trigger pushes the new value into the URL via
//      `router.replace` (no `push` — history stays clean).
//   4. Arrow-key navigation follows focus across the tab list.
//
// The render tree pulls in every child panel (Lifecycle, Cost-over-time,
// Execution, Expertise) — the api client is wrapped in a proxy that
// rejects any method we forgot to stub so a new fetch does not silently
// enter the tree and skew the assertions.

import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";

// ---------------------------------------------------------------------------
// Router + URL stubs — the test drives `?tab=` state through an
// observable `URLSearchParams`. `router.replace` updates the underlying
// value and notifies subscribers so `useSearchParams` re-runs the
// component and the controlled Tabs primitive picks up the new tab on
// the next render (matching the App Router's real behaviour).
// ---------------------------------------------------------------------------

const replaceMock = vi.fn();
let currentSearchParams = new URLSearchParams();
const searchParamsSubscribers = new Set<() => void>();

function setSearchParams(next: URLSearchParams) {
  currentSearchParams = next;
  searchParamsSubscribers.forEach((fn) => fn());
}

vi.mock("next/navigation", async () => {
  const [{ useSyncExternalStore }, { act }] = await Promise.all([
    import("react"),
    import("@testing-library/react"),
  ]);
  return {
    useRouter: () => ({
      push: vi.fn(),
      replace: (url: string) => {
        replaceMock(url);
        const qs = url.startsWith("?") ? url.slice(1) : "";
        // Wrap the store notification in `act` so the React warning
        // about unwrapped state updates stays quiet — our test mock is
        // the thing driving the update, and we want the re-render to
        // flush before the assertions run.
        act(() => {
          setSearchParams(new URLSearchParams(qs));
        });
      },
      refresh: vi.fn(),
      back: vi.fn(),
      prefetch: vi.fn(),
    }),
    usePathname: () => "/agents/alpha",
    useSearchParams: () => {
      // Subscribe the caller to `setSearchParams` so replacing the URL
      // causes the component to re-render with the new value — matches
      // the reactive contract of the real hook, which is all the detail
      // page needs to sync its controlled Tabs to `?tab=…`.
      return useSyncExternalStore(
        (notify) => {
          searchParamsSubscribers.add(notify);
          return () => searchParamsSubscribers.delete(notify);
        },
        () => currentSearchParams,
        () => currentSearchParams,
      );
    },
    notFound: () => {
      throw new Error("notFound");
    },
    redirect: (url: string) => {
      throw new Error(`redirect:${url}`);
    },
  };
});

// Activity stream is the one non-HTTP input to the page — stub it to an
// empty feed so none of the panels try to open a real EventSource.
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: () => ({ events: [], connected: false }),
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

// Suppress the toast surface — the assertions below care about the tab
// tree, not notification chrome.
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

// ---------------------------------------------------------------------------
// api client — every method the detail tree reaches for is stubbed
// below; anything we forgot rejects loudly so a missing stub surfaces
// immediately rather than yielding a spurious "timed out waiting for…".
// ---------------------------------------------------------------------------

const apiStub = {
  getAgent: vi.fn(),
  getAgentCost: vi.fn(),
  getClones: vi.fn(),
  getAgentBudget: vi.fn(),
  getAgentExpertise: vi.fn(),
  getAgentExecution: vi.fn(),
  getUnitExecution: vi.fn(),
  getPersistentAgentDeployment: vi.fn(),
  getProviderCredentialStatus: vi.fn(),
  getAgentRuntimeModels: vi.fn(),
  setAgentExpertise: vi.fn(),
  setAgentExecution: vi.fn(),
  setAgentBudget: vi.fn(),
  clearAgentExecution: vi.fn(),
  deleteAgent: vi.fn(),
  createClone: vi.fn(),
  deleteClone: vi.fn(),
  deployPersistentAgent: vi.fn(),
  undeployPersistentAgent: vi.fn(),
  scalePersistentAgent: vi.fn(),
  getPersistentAgentLogs: vi.fn(),
};

vi.mock("@/lib/api/client", () => ({
  api: new Proxy(apiStub, {
    get: (target, prop: string) => {
      if (prop in target) {
        return (target as Record<string, (...args: unknown[]) => unknown>)[
          prop
        ];
      }
      return () => Promise.reject(new Error(`Unstubbed api.${prop}`));
    },
  }),
}));

// ---------------------------------------------------------------------------
// Fixtures
// ---------------------------------------------------------------------------

const AGENT_ID = "alpha/one";

const baseAgent = {
  id: "agent-1",
  name: AGENT_ID,
  displayName: "Alpha One",
  description: "Primary analyst agent",
  role: "analyst",
  registeredAt: "2026-04-01T00:00:00Z",
  enabled: true,
  parentUnit: "alpha",
  executionMode: null,
};

type AgentDetailFixture = {
  agent: typeof baseAgent;
  deployment: null;
  status: { state: string; hostedOn: string } | null;
};

const baseDetail: AgentDetailFixture = {
  agent: baseAgent,
  deployment: null,
  status: null,
};

const detailWithStatus: AgentDetailFixture = {
  ...baseDetail,
  status: { state: "idle", hostedOn: "dapr" },
};

const baseCost = {
  totalCost: 0.42,
  totalInputTokens: 1000,
  totalOutputTokens: 500,
  recordCount: 4,
  initiativeCost: 0.1,
  workCost: 0.32,
  breakdowns: [],
};

const baseBudget = {
  agentId: AGENT_ID,
  dailyBudget: 5,
  updatedAt: "2026-04-10T00:00:00Z",
};

const emptyDeployment = {
  agentId: AGENT_ID,
  running: false,
  healthStatus: "unknown",
  replicas: 0,
  image: null,
  endpoint: null,
  containerId: null,
  startedAt: null,
  consecutiveFailures: 0,
};

const emptyExecution = {
  image: null,
  runtime: null,
  tool: null,
  provider: null,
  model: null,
  hosting: null,
};

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

async function renderDetail({
  initialTab,
  detail = baseDetail,
}: {
  initialTab?: string;
  detail?: AgentDetailFixture;
} = {}) {
  setSearchParams(
    new URLSearchParams(initialTab ? { tab: initialTab } : undefined),
  );
  apiStub.getAgent.mockResolvedValue(detail);
  apiStub.getAgentCost.mockResolvedValue(baseCost);
  apiStub.getClones.mockResolvedValue([]);
  apiStub.getAgentBudget.mockResolvedValue(baseBudget);
  apiStub.getAgentExpertise.mockResolvedValue([]);
  apiStub.getAgentExecution.mockResolvedValue(emptyExecution);
  apiStub.getUnitExecution.mockResolvedValue(emptyExecution);
  apiStub.getPersistentAgentDeployment.mockResolvedValue(emptyDeployment);
  apiStub.getProviderCredentialStatus.mockResolvedValue({
    provider: "openai",
    configured: false,
    hint: null,
  });
  apiStub.getAgentRuntimeModels.mockResolvedValue([]);

  const { default: AgentDetailClient } = await import("./agent-detail-client");

  const result = render(<AgentDetailClient id={AGENT_ID} />, {
    wrapper: Wrapper,
  });

  // Wait for the initial render past the skeleton — the heading only
  // appears once the agent query has resolved.
  await screen.findByRole("heading", { level: 1, name: /alpha one/i });
  return result;
}

// ---------------------------------------------------------------------------
// Specs
// ---------------------------------------------------------------------------

describe("AgentDetailClient — tabbed layout (#604)", () => {
  beforeEach(() => {
    replaceMock.mockReset();
    Object.values(apiStub).forEach((m) => m.mockReset());
    setSearchParams(new URLSearchParams());
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders the four tab triggers and defaults to Runtime", async () => {
    await renderDetail();

    // All three always-on triggers are present; Advanced is hidden when
    // `data.status` is null.
    expect(
      screen.getByRole("tab", { name: /interaction/i }),
    ).toBeInTheDocument();
    const runtimeTab = screen.getByRole("tab", { name: /runtime/i });
    expect(runtimeTab).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /settings/i })).toBeInTheDocument();
    expect(
      screen.queryByRole("tab", { name: /advanced/i }),
    ).not.toBeInTheDocument();

    // Runtime is the default open tab.
    expect(runtimeTab).toHaveAttribute("aria-selected", "true");

    // Runtime panel carries the lifecycle + cost cards.
    const panel = screen.getByRole("tabpanel", { name: /runtime/i });
    expect(
      within(panel).getByRole("heading", { name: /persistent deployment/i }),
    ).toBeInTheDocument();
    expect(
      within(panel).getByRole("heading", { name: /cost summary/i }),
    ).toBeInTheDocument();
    expect(
      within(panel).getByRole("heading", { name: /cost breakdown by activity/i }),
    ).toBeInTheDocument();
  });

  it("Interaction tab holds the conversation link and the Clones editor", async () => {
    await renderDetail();

    const interactionTrigger = screen.getByRole("tab", {
      name: /interaction/i,
    });
    act(() => {
      fireEvent.click(interactionTrigger);
    });

    const panel = screen.getByRole("tabpanel", { name: /interaction/i });
    expect(
      within(panel).getByTestId("agent-conversations-link"),
    ).toBeInTheDocument();
    expect(
      within(panel).getByRole("heading", { name: /clones/i }),
    ).toBeInTheDocument();
    // Runtime-only cards should not live here.
    expect(
      within(panel).queryByRole("heading", { name: /persistent deployment/i }),
    ).not.toBeInTheDocument();
  });

  it("Settings tab holds Agent Info, Daily Budget, Expertise, and Execution", async () => {
    await renderDetail();

    act(() => {
      fireEvent.click(screen.getByRole("tab", { name: /settings/i }));
    });

    const panel = screen.getByRole("tabpanel", { name: /settings/i });
    expect(
      within(panel).getByRole("heading", { name: /agent info/i }),
    ).toBeInTheDocument();
    expect(
      within(panel).getByRole("heading", { name: /daily budget/i }),
    ).toBeInTheDocument();
    expect(
      within(panel).getByRole("heading", { name: /expertise/i }),
    ).toBeInTheDocument();
    // Execution panel has its own queries (agent + unit execution) so
    // the header only appears once they resolve — wait for it.
    await waitFor(() => {
      expect(
        within(panel).getByRole("heading", { name: /execution/i }),
      ).toBeInTheDocument();
    });
  });

  it("Advanced tab is hidden when status is null and renders the JSON card when set", async () => {
    // No status → no Advanced trigger.
    const first = await renderDetail();
    expect(
      screen.queryByRole("tab", { name: /advanced/i }),
    ).not.toBeInTheDocument();
    // Tear down the first tree so its queries finish cleanly before we
    // mount the second fixture; otherwise the React test runner logs an
    // "update not wrapped in act" warning when the orphaned component
    // finishes its suspended read.
    first.unmount();

    // Re-mount with a populated status — Advanced appears and shows the
    // JSON payload once activated.
    await renderDetail({ detail: detailWithStatus });

    const advancedTrigger = await screen.findByRole("tab", {
      name: /advanced/i,
    });
    act(() => {
      fireEvent.click(advancedTrigger);
    });

    const panel = screen.getByRole("tabpanel", { name: /advanced/i });
    expect(
      within(panel).getByRole("heading", { name: /status/i }),
    ).toBeInTheDocument();
    expect(within(panel).getByText(/"state": "idle"/)).toBeInTheDocument();
  });

  it("`?tab=settings` on load opens Settings and tab clicks call router.replace", async () => {
    await renderDetail({ initialTab: "settings" });

    // Settings is active on first render — no extra user interaction.
    expect(
      screen.getByRole("tab", { name: /settings/i }),
    ).toHaveAttribute("aria-selected", "true");
    expect(
      screen.getByRole("tabpanel", { name: /settings/i }),
    ).toBeInTheDocument();

    // Switching back to Runtime should remove the `?tab=` param entirely
    // (Runtime is the default; the URL should collapse to `?`).
    act(() => {
      fireEvent.click(screen.getByRole("tab", { name: /runtime/i }));
    });
    expect(replaceMock).toHaveBeenLastCalledWith("?");

    // Clicking into Interaction adds the param back with the new value.
    act(() => {
      fireEvent.click(screen.getByRole("tab", { name: /interaction/i }));
    });
    expect(replaceMock).toHaveBeenLastCalledWith("?tab=interaction");
  });

  it("arrow-key navigation moves focus between triggers (follow focus)", async () => {
    await renderDetail();

    const interaction = screen.getByRole("tab", { name: /interaction/i });
    const runtime = screen.getByRole("tab", { name: /runtime/i });
    const settings = screen.getByRole("tab", { name: /settings/i });

    // Start from the default (Runtime). ArrowRight → Settings.
    runtime.focus();
    fireEvent.keyDown(runtime, { key: "ArrowRight" });
    expect(settings).toHaveAttribute("aria-selected", "true");

    // ArrowLeft from Runtime (still the key source) → Interaction.
    fireEvent.keyDown(runtime, { key: "ArrowLeft" });
    expect(interaction).toHaveAttribute("aria-selected", "true");
  });

  it("passes axe on the default (Runtime) render", async () => {
    const { container } = await renderDetail();
    await waitFor(() => {
      expect(
        screen.getByRole("tab", { name: /runtime/i }),
      ).toHaveAttribute("aria-selected", "true");
    });
    await expectNoAxeViolations(container);
  });
});
