import { render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  CredentialHealthResponse,
  InstalledAgentRuntimeResponse,
} from "@/lib/api/types";

const listAgentRuntimes =
  vi.fn<() => Promise<InstalledAgentRuntimeResponse[]>>();
const getAgentRuntimeCredentialHealth =
  vi.fn<(id: string) => Promise<CredentialHealthResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listAgentRuntimes: () => listAgentRuntimes(),
    getAgentRuntimeCredentialHealth: (id: string) =>
      getAgentRuntimeCredentialHealth(id),
  },
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

import SettingsAgentRuntimesPage from "./page";

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
  return render(<SettingsAgentRuntimesPage />, { wrapper: Wrapper });
}

function makeRuntime(
  overrides: Partial<InstalledAgentRuntimeResponse> = {},
): InstalledAgentRuntimeResponse {
  return {
    id: "claude",
    displayName: "Claude",
    toolKind: "claude-code-cli",
    installedAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-10T00:00:00Z",
    models: ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5"],
    defaultModel: "claude-opus-4-7",
    baseUrl: null,
    credentialKind: "ApiKey",
    credentialDisplayHint: "ANTHROPIC_API_KEY",
    ...overrides,
  } as InstalledAgentRuntimeResponse;
}

describe("SettingsAgentRuntimesPage", () => {
  beforeEach(() => {
    listAgentRuntimes.mockReset();
    getAgentRuntimeCredentialHealth.mockReset();
  });

  it("renders the h1 landmark (shared admin component)", async () => {
    listAgentRuntimes.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { level: 1, name: /agent runtimes/i }),
      ).toBeInTheDocument();
    });
  });

  it("renders installed runtimes with models, credential health, and CLI callout", async () => {
    listAgentRuntimes.mockResolvedValue([
      makeRuntime(),
      makeRuntime({
        id: "openai",
        displayName: "OpenAI",
        toolKind: "dapr-agent",
        models: ["gpt-4o", "gpt-4o-mini"],
        defaultModel: "gpt-4o",
      }),
    ]);
    getAgentRuntimeCredentialHealth.mockImplementation(async (id) => ({
      subjectId: id,
      secretName: "default",
      status: id === "claude" ? "Valid" : "Invalid",
      lastError: id === "claude" ? null : "401 Unauthorized",
      lastChecked: "2026-04-18T12:00:00Z",
    }));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("Claude")).toBeInTheDocument();
    });
    expect(screen.getByText("OpenAI")).toBeInTheDocument();
    expect(screen.getByText("claude-opus-4-7 · default")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o · default")).toBeInTheDocument();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-agent-runtime-health-claude"),
      ).toHaveTextContent("Valid");
    });
    expect(
      screen.getByTestId("admin-agent-runtime-health-openai"),
    ).toHaveTextContent("Invalid");
    expect(screen.getByText(/401 Unauthorized/)).toBeInTheDocument();

    expect(
      screen.getByText(/Read-only view — mutations go through the CLI\./i),
    ).toBeInTheDocument();
    expect(screen.getByText(/spring agent-runtime/i)).toBeInTheDocument();
  });

  it("renders the empty state when no runtimes are installed", async () => {
    listAgentRuntimes.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No agent runtimes installed on this tenant\./i),
      ).toBeInTheDocument();
    });
  });

  it("renders 'No signal yet' when the credential-health row is 404", async () => {
    listAgentRuntimes.mockResolvedValue([makeRuntime({ id: "google" })]);
    getAgentRuntimeCredentialHealth.mockResolvedValue(null);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-agent-runtime-health-google"),
      ).toHaveTextContent(/No signal yet/i);
    });
  });

  it("exposes no mutation controls (no install/uninstall/configure buttons)", async () => {
    listAgentRuntimes.mockResolvedValue([makeRuntime()]);
    getAgentRuntimeCredentialHealth.mockResolvedValue(null);

    const { container } = renderPage();

    await waitFor(() => {
      expect(screen.getByText("Claude")).toBeInTheDocument();
    });

    // The page must not render any buttons — all mutations are CLI-only.
    const buttons = within(container).queryAllByRole("button");
    expect(buttons).toHaveLength(0);

    // No forms either — the admin surface is purely display.
    expect(container.querySelector("form")).toBeNull();
    expect(container.querySelector("input")).toBeNull();
    expect(container.querySelector("select")).toBeNull();
    expect(container.querySelector("textarea")).toBeNull();
  });

  it("is axe-clean with populated data", async () => {
    listAgentRuntimes.mockResolvedValue([makeRuntime()]);
    getAgentRuntimeCredentialHealth.mockResolvedValue({
      subjectId: "claude",
      secretName: "default",
      status: "Valid",
      lastError: null,
      lastChecked: "2026-04-18T12:00:00Z",
    });

    const { container } = renderPage();
    await waitFor(() => {
      expect(screen.getByText("Claude")).toBeInTheDocument();
    });
    await expectNoAxeViolations(container);
  });
});
