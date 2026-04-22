import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  AgentExecutionResponse,
  ProviderCredentialStatusResponse,
  UnitExecutionResponse,
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
const getAgentRuntimeModels =
  vi.fn<
    (id: string) => Promise<
      { id: string; displayName: string; contextWindow: number | null }[]
    >
  >();
const getProviderCredentialStatus =
  vi.fn<
    (provider: string) => Promise<ProviderCredentialStatusResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgentExecution: (id: string) => getAgentExecution(id),
    setAgentExecution: (id: string, body: AgentExecutionResponse) =>
      setAgentExecution(id, body),
    clearAgentExecution: (id: string) => clearAgentExecution(id),
    getUnitExecution: (id: string) => getUnitExecution(id),
    getAgentRuntimeModels: (id: string) => getAgentRuntimeModels(id),
    getProviderCredentialStatus: (provider: string) =>
      getProviderCredentialStatus(provider),
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

describe("AgentExecutionPanel", () => {
  beforeEach(() => {
    getAgentExecution.mockReset();
    setAgentExecution.mockReset();
    clearAgentExecution.mockReset();
    getUnitExecution.mockReset();
    getAgentRuntimeModels.mockReset();
    getProviderCredentialStatus.mockReset();
    toastMock.mockReset();
    getAgentRuntimeModels.mockResolvedValue([]);
    getProviderCredentialStatus.mockResolvedValue({
      provider: "anthropic",
      resolvable: true,
      source: "tenant",
      suggestion: null,
    });
  });

  it("renders an 'inherited from unit' indicator when the agent leaves image blank and the unit has one", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "podman",
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

  it("hides Provider when the effective tool is codex (non-dapr-agent launcher)", async () => {
    // #641: Provider stays hidden for non-dapr-agent launchers, but the
    // Model dropdown is now rendered against the tool's catalog so the
    // operator can still pick a model family (e.g. gpt-4o for Codex).
    getAgentExecution.mockResolvedValue({ tool: "codex" });
    getUnitExecution.mockResolvedValue({});
    getAgentRuntimeModels.mockResolvedValue([{ id: "gpt-4o", displayName: "gpt-4o", contextWindow: null }, { id: "gpt-4o-mini", displayName: "gpt-4o-mini", contextWindow: null }]);

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
    getAgentExecution.mockResolvedValue({ tool: "codex" });
    getUnitExecution.mockResolvedValue({});
    getAgentRuntimeModels.mockImplementation(async (id: string) => {
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
      expect(getAgentRuntimeModels).toHaveBeenCalledWith("openai");
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

  it("shows both Provider and Model when tool=dapr-agent", async () => {
    getAgentExecution.mockResolvedValue({ tool: "dapr-agent" });
    getUnitExecution.mockResolvedValue({});
    getAgentRuntimeModels.mockResolvedValue([]);

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

  it("omits the Model dropdown when tool=custom (no known catalog)", async () => {
    getAgentExecution.mockResolvedValue({ tool: "custom" });
    getUnitExecution.mockResolvedValue({});
    getAgentRuntimeModels.mockResolvedValue([]);

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("agent-execution-panel");
    expect(
      screen.queryByTestId("agent-execution-provider-select"),
    ).not.toBeInTheDocument();
    // Neither a Model select nor a free-text Model input is rendered.
    expect(
      screen.queryByTestId("agent-execution-model-select"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("agent-execution-model-input"),
    ).not.toBeInTheDocument();
  });

  it("PUTs only the fields the operator declared, carrying nulls through unchanged slots", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
    });
    setAgentExecution.mockResolvedValue({ runtime: "podman" });

    render(
      <Wrapper>
        <AgentExecutionPanel agentId="alpha" parentUnitId="eng-team" />
      </Wrapper>,
    );

    const runtimeSelect = (await screen.findByTestId(
      "agent-execution-runtime-select",
    )) as HTMLSelectElement;
    fireEvent.change(runtimeSelect, { target: { value: "podman" } });

    fireEvent.click(screen.getByRole("button", { name: /^Save$/i }));

    await waitFor(() => {
      expect(setAgentExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setAgentExecution.mock.calls[0];
    expect(id).toBe("alpha");
    expect(body?.runtime).toBe("podman");
    // Image is still inherited — not explicitly set — so the wire
    // payload carries null, matching the backend's "resolve at
    // dispatch" contract.
    expect(body?.image).toBeNull();
  });

  it("renders axe-clean with the inherit indicator visible", async () => {
    getAgentExecution.mockResolvedValue({});
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "podman",
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
});
