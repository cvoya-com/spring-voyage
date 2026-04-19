import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import { expectNoAxeViolations } from "@/test/a11y";
import type { ProviderCredentialStatusResponse } from "@/lib/api/types";

// Mock the API client. Only the surface touched by the create wizard's
// Step 1 matters for these tests — we don't exercise unit creation here.
const listOllamaModels = vi.fn();
const listProviderModels = vi.fn();
const getProviderCredentialStatus = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listOllamaModels: () => listOllamaModels(),
    listProviderModels: (p: string) => listProviderModels(p),
    getProviderCredentialStatus: (p: string) => getProviderCredentialStatus(p),
    // Stubs for code paths we don't exercise on Step 1.
    getUnitTemplates: vi.fn().mockResolvedValue([]),
    getConnectorTypes: vi.fn().mockResolvedValue([]),
    createUnit: vi.fn(),
    createUnitFromTemplate: vi.fn(),
    createUnitFromYaml: vi.fn(),
    createUnitSecret: vi.fn(),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

const pushMock = vi.fn();
vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock }),
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

import CreateUnitPage from "./page";

function makeStatus(
  overrides: Partial<ProviderCredentialStatusResponse>,
): ProviderCredentialStatusResponse {
  return {
    provider: "anthropic",
    resolvable: true,
    source: "tenant",
    suggestion: null,
    ...overrides,
  };
}

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
  return render(<CreateUnitPage />, { wrapper: Wrapper });
}

async function selectTool(value: string) {
  const toolSelect = screen.getByLabelText("Execution tool") as HTMLSelectElement;
  await act(async () => {
    fireEvent.change(toolSelect, { target: { value } });
  });
}

describe("CreateUnitPage — Provider + Model gating (#598)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listOllamaModels.mockResolvedValue([]);
    listProviderModels.mockResolvedValue([]);
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "ollama", resolvable: true, source: null }),
    );
  });

  it("hides Provider + Model when the tool is Claude Code", async () => {
    renderPage();

    // Default tool is claude-code (see ai-models.ts).
    expect(
      screen.queryByLabelText(/^LLM provider$/i),
    ).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^Model$/i)).not.toBeInTheDocument();
  });

  it("hides Provider + Model for Codex, Gemini, and Custom", async () => {
    renderPage();
    for (const tool of ["codex", "gemini", "custom"]) {
      await selectTool(tool);
      expect(
        screen.queryByLabelText(/^LLM provider$/i),
      ).not.toBeInTheDocument();
      expect(screen.queryByLabelText(/^Model$/i)).not.toBeInTheDocument();
    }
  });

  it("renders Provider + Model only when the tool is Dapr Agent", async () => {
    renderPage();
    await selectTool("dapr-agent");

    expect(
      screen.getByLabelText(/^LLM provider$/i),
    ).toBeInTheDocument();
    expect(screen.getByLabelText(/^Model$/i)).toBeInTheDocument();
  });

  it("labels the provider field 'LLM Provider' unconditionally when shown", async () => {
    renderPage();
    await selectTool("dapr-agent");

    // No stale 'Provider' label — only the canonical 'LLM Provider'
    // spelling remains when the field is visible.
    expect(screen.queryAllByText(/^Provider$/).length).toBe(0);
    expect(screen.getByText(/^LLM Provider$/)).toBeInTheDocument();
  });
});

describe("CreateUnitPage — Credential status banner (#598)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    listOllamaModels.mockResolvedValue([]);
    listProviderModels.mockResolvedValue([]);
  });

  it("renders a 'tenant default' hint when credentials inherit from tenant", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "claude", resolvable: true, source: "tenant" }),
    );

    renderPage();
    await selectTool("dapr-agent");

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.resolvable).toBe("true");
    expect(status.dataset.source).toBe("tenant");
    expect(status.textContent).toMatch(/inherited from tenant default/i);
  });

  it("renders a 'set on unit' hint when credentials come from the unit", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ provider: "claude", resolvable: true, source: "unit" }),
    );

    renderPage();
    await selectTool("dapr-agent");

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.source).toBe("unit");
    expect(status.textContent).toMatch(/set on unit/i);
  });

  it("renders a 'not configured' warning with a deep-link when the provider has no credentials", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "claude",
        resolvable: false,
        source: null,
        suggestion:
          "Anthropic credentials are not configured. Set the tenant-default secret 'anthropic-api-key' …",
      }),
    );

    renderPage();
    await selectTool("dapr-agent");

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.resolvable).toBe("false");
    expect(status.textContent).toMatch(/not configured/i);
    // Deep-link into the Settings drawer's Tenant defaults panel
    // (PR #567 / PR #619).
    const link = screen.getByRole("link", { name: /tenant defaults/i });
    expect(link.getAttribute("href")).toBe("/?drawer=settings");
  });

  it("renders an 'Ollama unreachable' warning when the endpoint is down", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "ollama",
        resolvable: false,
        source: null,
        suggestion:
          "Ollama not reachable at http://spring-ollama:11434. Check that the Ollama server is running.",
      }),
    );

    renderPage();
    await selectTool("dapr-agent");

    // Switch the provider dropdown to Ollama so the badge re-queries
    // against the Ollama probe path.
    const providerSelect = screen.getByLabelText(
      /^LLM provider$/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(providerSelect, { target: { value: "ollama" } });
    });

    const status = await screen.findByTestId("credential-status");
    expect(status.dataset.resolvable).toBe("false");
    expect(status.textContent).toMatch(/Ollama not reachable/i);
    // Ollama has no tenant-defaults link — only the diagnostic message.
    expect(
      screen.queryByRole("link", { name: /tenant defaults/i }),
    ).not.toBeInTheDocument();
  });

  it("passes the selected provider id to the credential-status probe", async () => {
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({ resolvable: true, source: "tenant" }),
    );

    renderPage();
    await selectTool("dapr-agent");

    await waitFor(() => {
      expect(getProviderCredentialStatus).toHaveBeenCalledWith("claude");
    });

    const providerSelect = screen.getByLabelText(
      /^LLM provider$/i,
    ) as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(providerSelect, { target: { value: "openai" } });
    });

    await waitFor(() => {
      expect(getProviderCredentialStatus).toHaveBeenCalledWith("openai");
    });
  });

  it("passes axe a11y smoke with the 'not configured' warning visible", async () => {
    // Re-run the axe check because this banner is a new coloured-state
    // primitive in the create wizard. PR #599 / #610 pinned the
    // `warning/50`-border + `warning/15`-fill combination as axe-clean
    // at AA; if any future tailwind refactor drifts the tokens this
    // assertion will catch it.
    getProviderCredentialStatus.mockResolvedValue(
      makeStatus({
        provider: "claude",
        resolvable: false,
        source: null,
        suggestion: "Anthropic credentials are not configured.",
      }),
    );

    const { container } = renderPage();
    await selectTool("dapr-agent");
    await screen.findByTestId("credential-status");
    await expectNoAxeViolations(container);
  });
});
