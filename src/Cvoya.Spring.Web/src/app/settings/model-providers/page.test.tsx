import { render, screen, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  CredentialHealthResponse,
  InstalledModelProviderResponse,
} from "@/lib/api/types";

const listModelProviders =
  vi.fn<() => Promise<InstalledModelProviderResponse[]>>();
const getModelProviderCredentialHealth =
  vi.fn<(id: string) => Promise<CredentialHealthResponse | null>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    listModelProviders: () => listModelProviders(),
    getModelProviderCredentialHealth: (id: string) =>
      getModelProviderCredentialHealth(id),
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

import SettingsModelProvidersPage from "./page";

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
  return render(<SettingsModelProvidersPage />, { wrapper: Wrapper });
}

function makeProvider(
  overrides: Partial<InstalledModelProviderResponse> = {},
): InstalledModelProviderResponse {
  // ADR-0038: model-provider install rows carry id, displayName, models,
  // defaultModel, baseUrl, credentialKind, credentialDisplayHint, and
  // credentialSecretName. The legacy `kind` / `defaultImage` fields
  // belonged to the pre-ADR-0038 InstalledAgentRuntimeResponse and moved into
  // `runtime-catalog.yaml` — they're not on the wire anymore.
  return {
    id: "anthropic",
    displayName: "Anthropic",
    installedAt: "2026-04-01T00:00:00Z",
    updatedAt: "2026-04-10T00:00:00Z",
    models: ["claude-opus-4-7", "claude-sonnet-4-6", "claude-haiku-4-5"],
    defaultModel: "claude-opus-4-7",
    baseUrl: null,
    credentialKind: "ApiKey",
    credentialDisplayHint: "ANTHROPIC_API_KEY",
    credentialSecretName: "anthropic-api-key",
    ...overrides,
  } as InstalledModelProviderResponse;
}

describe("SettingsModelProvidersPage", () => {
  beforeEach(() => {
    listModelProviders.mockReset();
    getModelProviderCredentialHealth.mockReset();
  });

  it("renders the h1 landmark (shared admin component)", async () => {
    listModelProviders.mockResolvedValue([]);
    renderPage();
    await waitFor(() => {
      expect(
        screen.getByRole("heading", { level: 1, name: /model providers/i }),
      ).toBeInTheDocument();
    });
  });

  it("renders installed providers with models, credential health, and CLI callout", async () => {
    listModelProviders.mockResolvedValue([
      makeProvider(),
      makeProvider({
        id: "openai",
        displayName: "OpenAI",
        models: ["gpt-4o", "gpt-4o-mini"],
        defaultModel: "gpt-4o",
      }),
    ]);
    getModelProviderCredentialHealth.mockImplementation(async (id) => ({
      subjectId: id,
      secretName: "default",
      status: id === "anthropic" ? "Valid" : "Invalid",
      lastError: id === "anthropic" ? null : "401 Unauthorized",
      lastChecked: "2026-04-18T12:00:00Z",
    }));

    renderPage();

    await waitFor(() => {
      expect(screen.getByText("Anthropic")).toBeInTheDocument();
    });
    expect(screen.getByText("OpenAI")).toBeInTheDocument();
    expect(screen.getByText("claude-opus-4-7 · default")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o · default")).toBeInTheDocument();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-model-provider-health-anthropic"),
      ).toHaveTextContent("Valid");
    });
    expect(
      screen.getByTestId("admin-model-provider-health-openai"),
    ).toHaveTextContent("Invalid");
    expect(screen.getByText(/401 Unauthorized/)).toBeInTheDocument();

    expect(
      screen.getByText(/Read-only view — mutations go through the CLI\./i),
    ).toBeInTheDocument();
    expect(screen.getByText(/spring model-provider/i)).toBeInTheDocument();
  });

  it("renders the empty state when no providers are installed", async () => {
    listModelProviders.mockResolvedValue([]);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByText(/No model providers installed on this tenant\./i),
      ).toBeInTheDocument();
    });
  });

  it("renders 'No signal yet' when the credential-health row is 404", async () => {
    listModelProviders.mockResolvedValue([makeProvider({ id: "google" })]);
    getModelProviderCredentialHealth.mockResolvedValue(null);

    renderPage();

    await waitFor(() => {
      expect(
        screen.getByTestId("admin-model-provider-health-google"),
      ).toHaveTextContent(/No signal yet/i);
    });
  });

  it("exposes no mutation controls (no install/uninstall/configure buttons)", async () => {
    listModelProviders.mockResolvedValue([makeProvider()]);
    getModelProviderCredentialHealth.mockResolvedValue(null);

    const { container } = renderPage();

    await waitFor(() => {
      expect(screen.getByText("Anthropic")).toBeInTheDocument();
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
    listModelProviders.mockResolvedValue([makeProvider()]);
    getModelProviderCredentialHealth.mockResolvedValue({
      subjectId: "anthropic",
      secretName: "default",
      status: "Valid",
      lastError: null,
      lastChecked: "2026-04-18T12:00:00Z",
    });

    const { container } = renderPage();
    await waitFor(() => {
      expect(screen.getByText("Anthropic")).toBeInTheDocument();
    });
    await expectNoAxeViolations(container);
  });
});
