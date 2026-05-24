import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  AgentDetailResponse,
  AgentExecutionResponse,
  AgentResponse,
  ProviderCredentialStatusResponse,
  UnitExecutionResponse,
  UpdateAgentMetadataRequest,
} from "@/lib/api/types";

const getAgentExecution =
  vi.fn<(id: string) => Promise<AgentExecutionResponse>>();
const setAgentExecution =
  vi.fn<
    (
      id: string,
      body: AgentExecutionResponse,
    ) => Promise<AgentExecutionResponse>
  >();
const clearAgentExecution = vi.fn<(id: string) => Promise<void>>();
const getUnitExecution =
  vi.fn<(id: string) => Promise<UnitExecutionResponse>>();
const getAgent = vi.fn<(id: string) => Promise<AgentDetailResponse>>();
const updateAgentMetadata =
  vi.fn<(id: string, patch: UpdateAgentMetadataRequest) => Promise<unknown>>();
const getModelProviderModels =
  vi.fn<
    (id: string) => Promise<
      { id: string; displayName: string; contextWindow: number | null }[]
    >
  >();
const getProviderCredentialStatus =
  vi.fn<
    (
      provider: string,
      authMethod?: string,
    ) => Promise<ProviderCredentialStatusResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentExecution: (id: string) => getAgentExecution(id),
    setAgentExecution: (id: string, body: AgentExecutionResponse) =>
      setAgentExecution(id, body),
    clearAgentExecution: (id: string) => clearAgentExecution(id),
    getUnitExecution: (id: string) => getUnitExecution(id),
    getAgent: (id: string) => getAgent(id),
    updateAgentMetadata: (id: string, patch: UpdateAgentMetadataRequest) =>
      updateAgentMetadata(id, patch),
    getModelProviderModels: (id: string) => getModelProviderModels(id),
    listModelProviders: () => Promise.resolve([]),
    getProviderCredentialStatus: (
      provider: string,
      _agentImage?: string,
      authMethod?: string,
    ) => getProviderCredentialStatus(provider, authMethod),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
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

import { AgentExecutionPanel } from "./execution-panel";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function makeAgentDetail(
  overrides: Partial<AgentResponse> = {},
): AgentDetailResponse {
  const agent: AgentResponse = {
    id: "00000000-0000-0000-0000-000000000001",
    name: "alpha",
    displayName: "Alpha",
    description: "",
    role: null,
    registeredAt: "2026-05-12T00:00:00Z",
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "OnDemand",
    parentUnit: "eng-team",
    parentUnitId: "eng-team",
    hostingMode: null,
    initiativeLevel: null,
    lifecycleStatus: null,
    lifecycleError: null,
    instructions: null,
    effectiveTools: null,
    executionImage: null,
    systemPromptMode: null,
    declaredSystemPromptMode: null,
    ...overrides,
  };
  return { agent, status: null, deployment: null };
}

describe("AgentExecutionPanel", () => {
  beforeEach(() => {
    getAgentExecution.mockReset();
    setAgentExecution.mockReset();
    clearAgentExecution.mockReset();
    getUnitExecution.mockReset();
    getAgent.mockReset();
    updateAgentMetadata.mockReset();
    getModelProviderModels.mockReset();
    getProviderCredentialStatus.mockReset();
    toastMock.mockReset();
    getModelProviderModels.mockResolvedValue([]);
    getProviderCredentialStatus.mockResolvedValue({
      provider: "anthropic",
      resolvable: true,
      source: "tenant",
      suggestion: null,
    });
    // #2694: default agent detail with no declared / resolved
    // system_prompt_mode so the cascade lands on the platform
    // default. Individual tests override as needed.
    getAgent.mockResolvedValue(makeAgentDetail());
    updateAgentMetadata.mockResolvedValue(undefined);
  });

  it("renders an 'inherited from unit' indicator when the agent leaves image blank and the unit has one", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "claude-code",
    });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    // Wait until both queries have resolved so the help copy can pick
    // up the inherited value.
    await screen.findByTestId("agent-execution-panel");
    await waitFor(() => {
      expect(getUnitExecution).toHaveBeenCalledWith("eng-team");
    });
    await waitFor(() => {
      const indicators = screen.getAllByTestId("inherit-indicator");
      const texts = indicators.map((el) => el.textContent ?? "");
      expect(
        texts.some((t) =>
          t.includes("inherited from unit: ghcr.io/acme/spring-agent:v1"),
        ),
      ).toBe(true);
    });
  });

  it("does not render an inherit indicator for a field the agent explicitly set", async () => {
    getAgentExecution.mockResolvedValue({
      image: "ghcr.io/agents/alpha:custom",
    });
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
    });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    // Wait for both queries.
    await waitFor(() => {
      expect(getUnitExecution).toHaveBeenCalled();
    });

    // None of the indicator copy should mention the unit's image value,
    // because the agent overrode it.
    const indicators = screen.queryAllByTestId("inherit-indicator");
    for (const el of indicators) {
      expect(el.textContent ?? "").not.toContain("ghcr.io/acme/spring-agent:v1");
    }
  });

  it("hides Model Provider when the effective tool is codex (non-spring-voyage launcher)", async () => {
    // #641: Provider stays hidden for non-spring-voyage launchers, but the
    // Model dropdown is now rendered against the tool's catalog so the
    // operator can still pick a model family (e.g. gpt-4o for Codex).
    getAgentExecution.mockResolvedValue({ runtime: "codex" });
    getUnitExecution.mockResolvedValue({});
    getModelProviderModels.mockResolvedValue([{ id: "gpt-4o", displayName: "gpt-4o", contextWindow: null }, { id: "gpt-4o-mini", displayName: "gpt-4o-mini", contextWindow: null }]);

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    expect(
      screen.queryByTestId("agent-execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("renders a Model dropdown populated from the tool's catalog when tool=codex (#641)", async () => {
    getAgentExecution.mockResolvedValue({ runtime: "codex" });
    getUnitExecution.mockResolvedValue({});
    getModelProviderModels.mockImplementation(async (id: string) => {
      if (id === "openai") {
        return [
          { id: "gpt-4o", displayName: "gpt-4o", contextWindow: null },
          { id: "gpt-4o-mini", displayName: "gpt-4o-mini", contextWindow: null },
          { id: "o3-mini", displayName: "o3-mini", contextWindow: null },
        ];
      }
      return [];
    });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    await waitFor(() => {
      expect(getModelProviderModels).toHaveBeenCalledWith("openai");
    });

    const modelSelect = (await screen.findByTestId(
      "agent-execution-model-select",
    )) as HTMLSelectElement;
    const options = Array.from(modelSelect.options).map((o) => o.value);
    expect(options).toContain("gpt-4o");
    // Provider stays hidden — the tool implies it.
    expect(
      screen.queryByTestId("agent-execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("uses OAuth credential status for claude-code + anthropic", async () => {
    getAgentExecution.mockResolvedValue({ runtime: "claude-code" });
    getUnitExecution.mockResolvedValue({});
    getProviderCredentialStatus.mockResolvedValue({
      provider: "anthropic",
      resolvable: false,
      source: null,
      suggestion: null,
    });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const status = await screen.findByTestId("agent-execution-credential-status");
    expect(status).toHaveTextContent("Claude Code OAuth token");
    await waitFor(() => {
      expect(getProviderCredentialStatus).toHaveBeenCalledWith(
        "anthropic",
        "oauth",
      );
    });
  });

  it.each([
    ["codex", "openai", "OpenAI API key"],
    ["gemini", "google", "Google API key"],
    ["spring-voyage", "anthropic", "Anthropic API key"],
  ])(
    "uses API-key credential status for %s + %s",
    async (runtime, provider, label) => {
      getAgentExecution.mockResolvedValue({
        runtime,
        model: { provider, id: "model-id" },
      });
      getUnitExecution.mockResolvedValue({});
      getProviderCredentialStatus.mockResolvedValue({
        provider,
        resolvable: false,
        source: null,
        suggestion: null,
      });

      render(
        <Wrapper>
          <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
        </Wrapper>,
      );

      const status = await screen.findByTestId("agent-execution-credential-status");
      expect(status).toHaveTextContent(label);
      await waitFor(() => {
        expect(getProviderCredentialStatus).toHaveBeenCalledWith(
          provider,
          "api-key",
        );
      });
    },
  );

  it("shows both Model Provider and Model when tool=spring-voyage", async () => {
    getAgentExecution.mockResolvedValue({ runtime: "spring-voyage" });
    getUnitExecution.mockResolvedValue({});
    getModelProviderModels.mockResolvedValue([]);

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    expect(
      screen.getByTestId("agent-execution-provider-select"),
    ).toBeInTheDocument();
  });

  it("keeps the Model slot visible when tool=custom (always rendered post-#1702)", async () => {
    getAgentExecution.mockResolvedValue({ runtime: "custom" });
    getUnitExecution.mockResolvedValue({});
    getModelProviderModels.mockResolvedValue([]);

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    expect(
      screen.queryByTestId("agent-execution-provider-select"),
    ).not.toBeInTheDocument();
    // Model is always rendered — a free-text input is shown when no
    // catalog is available for the chosen tool.
    expect(
      screen.getByTestId("agent-execution-model-input"),
    ).toBeInTheDocument();
  });

  it("PUTs only the fields the operator declared, carrying nulls through unchanged slots; sends agent and drops runtime/tool", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
    });
    setAgentExecution.mockResolvedValue({ runtime: "claude-code" });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const toolSelect = (await screen.findByTestId(
      "agent-execution-runtime-select",
    )) as HTMLSelectElement;
    fireEvent.change(toolSelect, { target: { value: "claude-code" } });

    fireEvent.click(screen.getByRole("button", { name: /^Save$/i }));

    await waitFor(() => {
      expect(setAgentExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setAgentExecution.mock.calls[0];
    expect(id).toBe("alpha");
    // ADR-0038 (PR-1b): wire field renamed `agent` → `runtime`;
    // legacy `tool`, flat `provider`, and `kind` are gone.
    expect(body?.runtime).toBe("claude-code");
    expect((body as { agent?: string | null })?.agent).toBeUndefined();
    expect((body as { tool?: string | null })?.tool).toBeUndefined();
    expect((body as { provider?: string | null })?.provider).toBeUndefined();
    // Image is still inherited — not explicitly set — so the wire
    // payload carries null, matching the backend's "resolve at
    // dispatch" contract.
    expect(body?.image).toBeNull();
  });

  it("renders axe-clean with the inherit indicator visible", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "claude-code",
    });

    const { container } = render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    await waitFor(() => {
      expect(getUnitExecution).toHaveBeenCalled();
    });
    await expectNoAxeViolations(container);
  });

  it("renders the Hosting selector with friendly labels and saves the chosen mode (#1088)", async () => {
    // Issue #1088 — operators must be able to flip an agent's hosting
    // mode after create. The panel surfaces the same `HOSTING_MODES`
    // catalog the unit-create wizard uses (friendly "Ephemeral" /
    // "Persistent" labels) and PUTs the change through the existing
    // execution endpoint. The select is re-checked here to lock the
    // CLI-parity behaviour: changing → Save → server sees `persistent`.
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({});
    setAgentExecution.mockResolvedValue({ hosting: "persistent" });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const hostingSelect = (await screen.findByTestId(
      "agent-execution-hosting-select",
    )) as HTMLSelectElement;
    const optionLabels = Array.from(hostingSelect.options).map((o) => o.text);
    // Default placeholder + the two friendly labels — never raw ids.
    expect(optionLabels).toContain("(leave to default)");
    expect(optionLabels).toContain("Ephemeral");
    expect(optionLabels).toContain("Persistent");

    fireEvent.change(hostingSelect, { target: { value: "persistent" } });
    fireEvent.click(screen.getByRole("button", { name: /^Save$/i }));

    await waitFor(() => {
      expect(setAgentExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setAgentExecution.mock.calls[0];
    expect(id).toBe("alpha");
    expect(body?.hosting).toBe("persistent");
  });

  it("clears the Hosting field via the per-row Clear affordance (#1088)", async () => {
    // CLI mirror: `spring agent execution clear --field hosting`. When
    // the agent has its own hosting value alongside other declared
    // fields, the FieldRow exposes a Clear button; clicking it PUTs the
    // block back with `hosting: null` and leaves the other fields
    // untouched.
    getAgentExecution.mockResolvedValue({
      image: "ghcr.io/agents/alpha:custom",
      hosting: "persistent",
    });
    getUnitExecution.mockResolvedValue({});
    setAgentExecution.mockResolvedValue({
      image: "ghcr.io/agents/alpha:custom",
    });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const clearHostingBtn = await screen.findByTestId(
      "agent-execution-clear-hosting",
    );
    fireEvent.click(clearHostingBtn);

    await waitFor(() => {
      expect(setAgentExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setAgentExecution.mock.calls[0];
    expect(id).toBe("alpha");
    expect(body?.hosting).toBeNull();
    // Other declared fields ride through unchanged — clear-one-field
    // semantics, not clear-all.
    expect(body?.image).toBe("ghcr.io/agents/alpha:custom");
  });

  it("falls back to DELETE when the operator clears every agent-owned field", async () => {
    getAgentExecution.mockResolvedValue({
      image: "ghcr.io/agents/alpha:custom",
    });
    getUnitExecution.mockResolvedValue({});
    clearAgentExecution.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const clearImageBtn = await screen.findByTestId(
      "agent-execution-clear-image",
    );
    fireEvent.click(clearImageBtn);

    await waitFor(() => {
      expect(clearAgentExecution).toHaveBeenCalledTimes(1);
    });
    expect(clearAgentExecution).toHaveBeenCalledWith("alpha");
  });

  // ---------------------------------------------------------------------
  // system_prompt_mode (#2694 — N4 of #2667 / #2691 / #2692).
  // ---------------------------------------------------------------------

  it("renders the Default cascade indicator when neither agent nor unit declared a mode", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({});
    getAgent.mockResolvedValue(makeAgentDetail({ systemPromptMode: "append" }));

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const indicator = await screen.findByTestId(
      "agent-system-prompt-mode-cascade-indicator",
    );
    expect(indicator).toHaveAttribute("data-origin", "default");
    expect(indicator).toHaveTextContent("Default");

    // Effective default falls back to Append.
    const appendOption = screen.getByTestId(
      "agent-system-prompt-mode-option-append",
    );
    expect(appendOption).toHaveAttribute("aria-checked", "true");
  });

  it("renders 'Inherited from unit' when only the unit declared a mode", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({ systemPromptMode: "replace" });
    getAgent.mockResolvedValue(
      makeAgentDetail({
        // The API resolves to the unit's value.
        systemPromptMode: "replace",
        // No agent-declared value.
        declaredSystemPromptMode: null,
      }),
    );

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const indicator = await screen.findByTestId(
      "agent-system-prompt-mode-cascade-indicator",
    );
    await waitFor(() => {
      expect(indicator).toHaveAttribute("data-origin", "unit");
    });
    expect(indicator).toHaveTextContent("Inherited from unit");
    const replaceOption = screen.getByTestId(
      "agent-system-prompt-mode-option-replace",
    );
    expect(replaceOption).toHaveAttribute("aria-checked", "true");
    // No "Clear override" because nothing is declared on the agent yet.
    expect(
      screen.queryByTestId("agent-system-prompt-mode-clear"),
    ).not.toBeInTheDocument();
  });

  it("renders 'Set here' and exposes Clear override when the agent declared the value", async () => {
    getAgentExecution.mockResolvedValue({ systemPromptMode: "replace" });
    getUnitExecution.mockResolvedValue({});
    getAgent.mockResolvedValue(
      makeAgentDetail({
        systemPromptMode: "replace",
        declaredSystemPromptMode: "replace",
      }),
    );

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const indicator = await screen.findByTestId(
      "agent-system-prompt-mode-cascade-indicator",
    );
    await waitFor(() => {
      expect(indicator).toHaveAttribute("data-origin", "agent");
    });
    expect(indicator).toHaveTextContent("Set here");
    expect(
      screen.getByTestId("agent-system-prompt-mode-clear"),
    ).toBeInTheDocument();
  });

  it("sends a PATCH with the chosen literal when the operator picks an option", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({});
    getAgent.mockResolvedValue(makeAgentDetail({ systemPromptMode: "append" }));

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const replaceOption = await screen.findByTestId(
      "agent-system-prompt-mode-option-replace",
    );
    fireEvent.click(replaceOption);

    await waitFor(() => {
      expect(updateAgentMetadata).toHaveBeenCalledTimes(1);
    });
    expect(updateAgentMetadata).toHaveBeenCalledWith("alpha", {
      systemPromptMode: "replace",
    });
  });

  it("Clear override issues a PATCH with explicit null", async () => {
    getAgentExecution.mockResolvedValue({ systemPromptMode: "replace" });
    getUnitExecution.mockResolvedValue({});
    getAgent.mockResolvedValue(
      makeAgentDetail({
        systemPromptMode: "replace",
        declaredSystemPromptMode: "replace",
      }),
    );

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const clearBtn = await screen.findByTestId("agent-system-prompt-mode-clear");
    fireEvent.click(clearBtn);

    await waitFor(() => {
      expect(updateAgentMetadata).toHaveBeenCalledTimes(1);
    });
    const [id, patch] = updateAgentMetadata.mock.calls[0];
    expect(id).toBe("alpha");
    // Critically: an explicit JSON null (not undefined) so the
    // tri-state API clears the slot rather than leaving it alone.
    expect(patch).toEqual({ systemPromptMode: null });
    expect(Object.prototype.hasOwnProperty.call(patch, "systemPromptMode")).toBe(
      true,
    );
  });
});
