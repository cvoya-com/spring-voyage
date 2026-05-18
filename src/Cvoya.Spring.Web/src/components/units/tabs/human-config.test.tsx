import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { EXPLORER_URL_CHANGE_EVENT } from "@/lib/explorer-url";

import type { AgentNode, HumanNode } from "../aggregate";

import HumanConfigTab from "./human-config";

// ---------------------------------------------------------------------------
// Module mocks
// ---------------------------------------------------------------------------

const mockListIdentities = vi.fn();
const mockUpsertIdentity = vi.fn();
const mockRemoveIdentity = vi.fn();
const mockListConnectors = vi.fn();
const mockGetHuman = vi.fn();
const mockGetCurrentUser = vi.fn();
const mockToast = vi.fn();

vi.mock("@/lib/api/client", async () => {
  // Preserve the real ApiError class so the component's instanceof check
  // surfaces ProblemDetails copy on simulated 409s.
  const actual =
    await vi.importActual<typeof import("@/lib/api/client")>(
      "@/lib/api/client",
    );
  return {
    ...actual,
    api: {
      listHumanIdentities: (...args: unknown[]) =>
        mockListIdentities(...args),
      upsertHumanIdentity: (...args: unknown[]) =>
        mockUpsertIdentity(...args),
      removeHumanIdentity: (...args: unknown[]) =>
        mockRemoveIdentity(...args),
      listConnectors: (...args: unknown[]) => mockListConnectors(...args),
      getHuman: (...args: unknown[]) => mockGetHuman(...args),
      getCurrentUser: (...args: unknown[]) => mockGetCurrentUser(...args),
    },
  };
});

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: mockToast }),
}));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

const HUMAN_ID = "11111111-1111-1111-1111-111111111111";

const humanNode: HumanNode = {
  kind: "Human",
  id: HUMAN_ID,
  name: "Savas",
  status: "running",
};

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function setSearchParams(next: URLSearchParams) {
  const qs = next.toString();
  window.history.replaceState(
    null,
    "",
    qs ? `${window.location.pathname}?${qs}` : window.location.pathname,
  );
  act(() => {
    window.dispatchEvent(new Event(EXPLORER_URL_CHANGE_EVENT));
  });
}

// ---------------------------------------------------------------------------

describe("HumanConfigTab — Identity + Connector sub-tabs (#2269)", () => {
  beforeEach(() => {
    mockListIdentities.mockReset();
    mockUpsertIdentity.mockReset();
    mockRemoveIdentity.mockReset();
    mockListConnectors.mockReset();
    mockGetHuman.mockReset();
    mockGetCurrentUser.mockReset();
    mockToast.mockReset();

    // Default-safe stubs — individual tests override per scenario.
    mockListIdentities.mockResolvedValue([]);
    mockListConnectors.mockResolvedValue([]);
    mockGetHuman.mockResolvedValue({
      id: HUMAN_ID,
      username: "savas",
      displayName: "Savas",
      email: "savas@example.com",
      platformRole: "Operator",
      createdAt: "2026-05-01T00:00:00Z",
    });
    mockGetCurrentUser.mockResolvedValue(null);

    // Reset the URL between tests.
    window.history.replaceState(null, "", window.location.pathname);
  });

  afterEach(() => {
    window.history.replaceState(null, "", window.location.pathname);
  });

  // -------------------------------------------------------------------------
  // Slot guards
  // -------------------------------------------------------------------------

  it("renders nothing for a non-Human node (defensive — registry guards this)", () => {
    const agent: AgentNode = {
      kind: "Agent",
      id: "ada",
      name: "Ada",
      status: "running",
    };
    const { container } = render(
      <Wrapper>
        <HumanConfigTab node={agent} path={[agent]} />
      </Wrapper>,
    );
    expect(container.firstChild).toBeNull();
  });

  // -------------------------------------------------------------------------
  // Identity sub-tab rendering
  // -------------------------------------------------------------------------

  it("renders the Identity loading skeleton while the list resolves", async () => {
    let resolve: (rows: unknown[]) => void = () => undefined;
    mockListIdentities.mockImplementationOnce(
      () =>
        new Promise((r) => {
          resolve = r as (rows: unknown[]) => void;
        }),
    );

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-identity-loading"),
      ).toBeInTheDocument();
    });

    act(() => {
      resolve([]);
    });
  });

  it("renders the empty state when the human has no identities", async () => {
    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-identity-empty"),
      ).toBeInTheDocument();
    });
    expect(
      screen.getByTestId("tab-human-config-identity-add"),
    ).toBeInTheDocument();
  });

  it("renders one row per connector-identity entry", async () => {
    mockListIdentities.mockResolvedValueOnce([
      {
        humanId: HUMAN_ID,
        connectorId: "github",
        connectorUserId: "savas",
        displayHandle: "@savas",
        createdAt: "2026-05-01T00:00:00Z",
        updatedAt: "2026-05-01T00:00:00Z",
      },
      {
        humanId: HUMAN_ID,
        connectorId: "slack",
        connectorUserId: "U123",
        displayHandle: null,
        createdAt: "2026-05-01T00:00:00Z",
        updatedAt: "2026-05-01T00:00:00Z",
      },
    ]);

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-identity-list"),
      ).toBeInTheDocument();
    });

    const rows = screen.getAllByTestId("tab-human-config-identity-row");
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveTextContent("github");
    expect(rows[0]).toHaveTextContent("savas");
    expect(rows[0]).toHaveTextContent("@savas");
    expect(rows[1]).toHaveTextContent("slack");
    expect(rows[1]).toHaveTextContent("U123");
  });

  // -------------------------------------------------------------------------
  // "You" hint
  // -------------------------------------------------------------------------

  it("renders the You hint when the active human is the authenticated caller", async () => {
    mockGetCurrentUser.mockResolvedValueOnce({
      id: HUMAN_ID,
      username: "savas",
      displayName: "Savas",
      platformRole: "Owner",
    });

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-you-hint"),
      ).toBeInTheDocument();
    });
  });

  it("does not render the You hint for a different human", async () => {
    mockGetCurrentUser.mockResolvedValueOnce({
      id: "22222222-2222-2222-2222-222222222222",
      username: "other",
      displayName: "Other",
      platformRole: "Operator",
    });

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId("tab-human-config")).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("tab-human-config-you-hint"),
    ).not.toBeInTheDocument();
  });

  // -------------------------------------------------------------------------
  // Add form
  // -------------------------------------------------------------------------

  it("submits the add form against the upsert endpoint with the right payload", async () => {
    mockListConnectors.mockResolvedValueOnce([
      {
        typeId: "github-id",
        typeSlug: "github",
        displayName: "GitHub",
        description: "",
        configUrl: "",
        actionsBaseUrl: "",
        configSchemaUrl: "",
        installedAt: "2026-05-01T00:00:00Z",
        updatedAt: "2026-05-01T00:00:00Z",
        config: {},
        toolNamespace: "github",
      },
    ]);
    mockUpsertIdentity.mockResolvedValueOnce({
      humanId: HUMAN_ID,
      connectorId: "github",
      connectorUserId: "octocat",
      displayHandle: "@octocat",
      createdAt: "2026-05-01T00:00:00Z",
      updatedAt: "2026-05-01T00:00:00Z",
    });

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    // Wait for the form + catalog to settle.
    await waitFor(() => {
      const select = screen.getByTestId(
        "tab-human-config-identity-connector",
      ) as HTMLSelectElement;
      expect(select.value).toBe("github");
    });

    fireEvent.change(
      screen.getByTestId("tab-human-config-identity-user-id"),
      { target: { value: "octocat" } },
    );
    fireEvent.change(
      screen.getByTestId("tab-human-config-identity-handle"),
      { target: { value: "@octocat" } },
    );

    fireEvent.click(
      screen.getByTestId("tab-human-config-identity-add-submit"),
    );

    await waitFor(() => {
      expect(mockUpsertIdentity).toHaveBeenCalledTimes(1);
    });
    expect(mockUpsertIdentity).toHaveBeenCalledWith(HUMAN_ID, {
      connectorId: "github",
      connectorUserId: "octocat",
      displayHandle: "@octocat",
    });
  });

  it("surfaces an inline error when the upsert returns 409", async () => {
    mockListConnectors.mockResolvedValueOnce([
      {
        typeId: "github-id",
        typeSlug: "github",
        displayName: "GitHub",
        description: "",
        configUrl: "",
        actionsBaseUrl: "",
        configSchemaUrl: "",
        installedAt: "2026-05-01T00:00:00Z",
        updatedAt: "2026-05-01T00:00:00Z",
        config: {},
        toolNamespace: "github",
      },
    ]);

    const { ApiError } = await import("@/lib/api/client");
    mockUpsertIdentity.mockRejectedValueOnce(
      new ApiError(409, "Conflict", {
        type: "/problems/conflict",
        title: "Connector identity already claimed",
        detail:
          "Connector identity 'github:octocat' is already mapped to a different human.",
        status: 409,
      }),
    );

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      const select = screen.getByTestId(
        "tab-human-config-identity-connector",
      ) as HTMLSelectElement;
      expect(select.value).toBe("github");
    });

    fireEvent.change(
      screen.getByTestId("tab-human-config-identity-user-id"),
      { target: { value: "octocat" } },
    );
    fireEvent.click(
      screen.getByTestId("tab-human-config-identity-add-submit"),
    );

    await waitFor(() => {
      const err = screen.getByTestId(
        "tab-human-config-identity-add-error",
      );
      expect(err).toHaveTextContent(/already mapped/i);
    });
  });

  // -------------------------------------------------------------------------
  // Delete confirmation
  // -------------------------------------------------------------------------

  it("opens the confirmation modal and DELETEs against the right endpoint", async () => {
    mockListIdentities.mockResolvedValueOnce([
      {
        humanId: HUMAN_ID,
        connectorId: "github",
        connectorUserId: "savas",
        displayHandle: null,
        createdAt: "2026-05-01T00:00:00Z",
        updatedAt: "2026-05-01T00:00:00Z",
      },
    ]);
    mockRemoveIdentity.mockResolvedValueOnce(undefined);

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-identity-remove"),
      ).toBeInTheDocument();
    });

    fireEvent.click(
      screen.getByTestId("tab-human-config-identity-remove"),
    );

    // Confirm dialog renders with the destructive copy.
    const confirm = await screen.findByRole("button", { name: /^Remove$/ });
    fireEvent.click(confirm);

    await waitFor(() => {
      expect(mockRemoveIdentity).toHaveBeenCalledTimes(1);
    });
    expect(mockRemoveIdentity).toHaveBeenCalledWith(
      HUMAN_ID,
      "github",
      "savas",
    );
  });

  // -------------------------------------------------------------------------
  // Sub-tab routing — Identity default, Connector caveat reachable via subtab=
  // -------------------------------------------------------------------------

  it("defaults to the Identity sub-tab when ?subtab= is absent", async () => {
    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-identity"),
      ).toBeInTheDocument();
    });
    expect(
      screen.queryByTestId("tab-human-config-connector"),
    ).not.toBeInTheDocument();
  });

  it("renders the Connector caveat sub-tab when ?subtab=Connector is set", async () => {
    setSearchParams(new URLSearchParams({ subtab: "Connector" }));

    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-connector"),
      ).toBeInTheDocument();
    });
    // Identity sub-tab body is unmounted (TabsContent only renders the
    // active panel) so the empty/loading testids are not present.
    expect(
      screen.queryByTestId("tab-human-config-identity"),
    ).not.toBeInTheDocument();
  });

  it("writes ?subtab=Connector to the URL when the Connector trigger is clicked", async () => {
    render(
      <Wrapper>
        <HumanConfigTab node={humanNode} path={[humanNode]} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(screen.getByTestId("tab-human-config")).toBeInTheDocument();
    });

    const trigger = screen.getByRole("tab", { name: "Connector" });
    fireEvent.click(trigger);

    expect(window.location.search).toContain("subtab=Connector");
    await waitFor(() => {
      expect(
        screen.getByTestId("tab-human-config-connector"),
      ).toBeInTheDocument();
    });
  });
});
