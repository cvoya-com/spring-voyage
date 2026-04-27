import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  CredentialHealthResponse,
  InstalledConnectorResponse,
} from "@/lib/api/types";

// ---------------------------------------------------------------------------
// Router + URL stubs — drive `?tab=` state through an observable
// `URLSearchParams`. `router.replace` updates the underlying value and
// notifies subscribers so `useSearchParams` re-runs the component and
// the controlled `<Tabs>` primitive picks up the new tab on the next
// render. Matches the mock shape used by the agent-detail test.
// ---------------------------------------------------------------------------

const replaceMock = vi.fn();
let currentSearchParams = new URLSearchParams();
const searchParamsSubscribers = new Set<() => void>();

function setSearchParams(next: URLSearchParams) {
  currentSearchParams = next;
  searchParamsSubscribers.forEach((fn) => fn());
}

vi.mock("next/navigation", async () => {
  const [{ useSyncExternalStore }, { act: rtlAct }] = await Promise.all([
    import("react"),
    import("@testing-library/react"),
  ]);
  return {
    useRouter: () => ({
      push: vi.fn(),
      replace: (url: string, opts?: { scroll?: boolean }) => {
        replaceMock(url, opts);
        // Accept both `?foo=bar` and `/path?foo=bar` — the page now passes
        // the pathname alongside the query (#1053) because Next.js 16's
        // `router.replace("?…")` dropped the canonical-URL update.
        const qIdx = url.indexOf("?");
        const qs = qIdx >= 0 ? url.slice(qIdx + 1) : "";
        rtlAct(() => {
          setSearchParams(new URLSearchParams(qs));
        });
      },
      refresh: vi.fn(),
      back: vi.fn(),
      prefetch: vi.fn(),
    }),
    usePathname: () => "/connectors",
    useSearchParams: () => {
      return useSyncExternalStore(
        (notify) => {
          searchParamsSubscribers.add(notify);
          return () => searchParamsSubscribers.delete(notify);
        },
        () => currentSearchParams,
        () => currentSearchParams,
      );
    },
  };
});

const listConnectors = vi.fn<() => Promise<InstalledConnectorResponse[]>>();
const getConnectorCredentialHealth =
  vi.fn<(slug: string) => Promise<CredentialHealthResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listConnectors: () => listConnectors(),
    getConnectorCredentialHealth: (slug: string) =>
      getConnectorCredentialHealth(slug),
  },
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

import ConnectorsListPage from "./page";

function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
      mutations: { retry: false },
    },
  });
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={client}>{children}</QueryClientProvider>
  );
  return render(<ConnectorsListPage />, { wrapper: Wrapper });
}

function makeConnector(
  overrides: Partial<InstalledConnectorResponse> = {},
): InstalledConnectorResponse {
  return {
    typeId: "github-id",
    typeSlug: "github",
    displayName: "GitHub",
    description: "Listen to GitHub webhooks.",
    configUrl: "/api/v1/tenant/connectors/github/units/{unitId}/config",
    actionsBaseUrl: "/api/v1/tenant/connectors/github/actions",
    configSchemaUrl: "/api/v1/tenant/connectors/github/config-schema",
    installedAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-10T00:00:00Z",
    config: null,
    ...overrides,
  } as InstalledConnectorResponse;
}

describe("ConnectorsListPage", () => {
  beforeEach(() => {
    listConnectors.mockReset();
    getConnectorCredentialHealth.mockReset();
    replaceMock.mockReset();
    act(() => {
      setSearchParams(new URLSearchParams());
    });
  });

  it("renders one card per installed connector with a link to its detail page", async () => {
    listConnectors.mockResolvedValue([
      makeConnector(),
      makeConnector({
        typeId: "slack-id",
        typeSlug: "slack",
        displayName: "Slack",
        description: "Slack messages.",
      }),
    ]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });
    expect(screen.getByText("Slack")).toBeInTheDocument();

    const githubLink = screen
      .getByLabelText("Open connector GitHub")
      .closest("a");
    expect(githubLink).toHaveAttribute("href", "/connectors/github");
  });

  it("renders the empty state when no connectors are installed on the tenant", async () => {
    listConnectors.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No connectors installed on this tenant\./i),
      ).toBeInTheDocument();
    });
    // Post-`DEL-packages-top` (#874) the `/packages` link is gone;
    // the empty-state copy now only mentions the CLI install verb.
    expect(
      screen.queryByRole("link", { name: /Packages/i }),
    ).not.toBeInTheDocument();
  });

  it("renders a two-tab layout with Catalog as the default tab", async () => {
    listConnectors.mockResolvedValue([makeConnector()]);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });

    const tabs = screen.getAllByRole("tab");
    expect(tabs).toHaveLength(2);
    expect(tabs[0]).toHaveTextContent(/catalog/i);
    expect(tabs[1]).toHaveTextContent(/health/i);
    expect(tabs[0]).toHaveAttribute("aria-selected", "true");
    expect(tabs[1]).toHaveAttribute("aria-selected", "false");
  });

  it("pre-selects the Health tab when the URL already carries ?tab=health", async () => {
    act(() => {
      setSearchParams(new URLSearchParams("tab=health"));
    });
    listConnectors.mockResolvedValue([makeConnector()]);
    getConnectorCredentialHealth.mockResolvedValue({
      subjectId: "github",
      secretName: "default",
      status: "Valid",
      lastError: null,
      lastChecked: "2026-04-18T12:00:00Z",
    });

    renderPage();

    await waitFor(() => {
      const healthTab = screen.getByRole("tab", { name: /health/i });
      expect(healthTab).toHaveAttribute("aria-selected", "true");
    });

    // The Health tab renders the shared read-only panel with the CLI
    // callout, so a visible "Read-only view…" banner confirms the panel
    // actually mounted under the Health tab.
    await waitFor(() => {
      expect(
        screen.getByText(/Read-only view — mutations go through the CLI\./i),
      ).toBeInTheDocument();
    });
  });

  it("mirrors the active tab into the URL when the user clicks Health", async () => {
    listConnectors.mockResolvedValue([makeConnector()]);
    getConnectorCredentialHealth.mockResolvedValue(null);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("GitHub")).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole("tab", { name: /health/i }));

    // #1053: navigation must be `/connectors?tab=health`, not the bare
    // `?tab=health`. Next.js 16 silently drops the canonical-URL update
    // when the relative URL is query-only, which leaves the controlled
    // `<Tabs value={activeTab}>` snapping back to the prior tab.
    await waitFor(() => {
      expect(replaceMock).toHaveBeenCalledWith("/connectors?tab=health", {
        scroll: false,
      });
    });
  });
});
