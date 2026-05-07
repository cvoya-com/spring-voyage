import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { expectNoAxeViolations } from "@/test/a11y";
import type {
  ProviderCredentialStatusResponse,
  UnitExecutionResponse,
} from "@/lib/api/types";

const getUnitExecution = vi.fn<(id: string) => Promise<UnitExecutionResponse>>();
const setUnitExecution =
  vi.fn<
    (
      id: string,
      body: UnitExecutionResponse,
    ) => Promise<UnitExecutionResponse>
  >();
const clearUnitExecution = vi.fn<(id: string) => Promise<void>>();
const getAgentRuntimeModels = vi.fn<(id: string) => Promise<{ id: string; displayName: string; contextWindow: number | null }[]>>();
const getProviderCredentialStatus =
  vi.fn<
    (provider: string) => Promise<ProviderCredentialStatusResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitExecution: (id: string) => getUnitExecution(id),
    setUnitExecution: (id: string, body: UnitExecutionResponse) =>
      setUnitExecution(id, body),
    clearUnitExecution: (id: string) => clearUnitExecution(id),
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

import { ExecutionTab } from "./execution-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("ExecutionTab", () => {
  beforeEach(() => {
    getUnitExecution.mockReset();
    setUnitExecution.mockReset();
    clearUnitExecution.mockReset();
    getAgentRuntimeModels.mockReset();
    getProviderCredentialStatus.mockReset();
    toastMock.mockReset();
    // Default: no models fetched + no credential probe so the banner
    // doesn't pop up unless the test sets it.
    getAgentRuntimeModels.mockResolvedValue([]);
    getProviderCredentialStatus.mockResolvedValue({
      provider: "anthropic",
      resolvable: true,
      source: "tenant",
      suggestion: null,
    });
  });

  it("renders Image, Agent Runtime and Model fields by default; Model Provider hidden until agent=spring-voyage", async () => {
    getUnitExecution.mockResolvedValue({});

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("unit-execution-card");
    expect(
      screen.getByTestId("execution-image-input"),
    ).toBeInTheDocument();
    // #1702: container Runtime row was removed; the agent-runtime select
    // (`execution-agent-runtime-select`) is the only runtime-shaped control
    // that should render.
    expect(screen.getByTestId("execution-agent-runtime-select")).toBeInTheDocument();
    // agent unset → Model Provider hidden (only renders for spring-voyage runtime kind).
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
    // Model is always rendered — starts as plain input because no
    // catalog has loaded.
    expect(screen.getByTestId("execution-model-input")).toBeInTheDocument();
  });

  it("hides Model Provider but keeps Model visible when agent is claude-code (#641)", async () => {
    // #641 (PR #645 on wizard/agent; this issue is the unit tab parity):
    // Provider stays hidden for non-spring-voyage launchers, but the Model
    // dropdown is now rendered against the runtime's catalog so the
    // operator can still pick a model family (e.g. claude-opus-4 for
    // Claude Code).
    getUnitExecution.mockResolvedValue({ runtime: "claude-code" });
    getAgentRuntimeModels.mockResolvedValue([
      { id: "claude-sonnet-4-6", displayName: "claude-sonnet-4-6", contextWindow: null },
      { id: "claude-opus-4-7", displayName: "claude-opus-4-7", contextWindow: null },
    ]);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("execution-agent-runtime-select");
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("renders a Model dropdown populated from the runtime's catalog when agent=codex (#641)", async () => {
    getUnitExecution.mockResolvedValue({ runtime: "codex" });
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
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("unit-execution-card");
    await waitFor(() => {
      expect(getAgentRuntimeModels).toHaveBeenCalledWith("openai");
    });

    const modelSelect = (await screen.findByTestId(
      "execution-model-select",
    )) as HTMLSelectElement;
    const options = Array.from(modelSelect.options).map((o) => o.value);
    expect(options).toContain("gpt-4o");
    // Model Provider stays hidden — the tool implies it.
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("shows both Model Provider and Model when agent=spring-voyage (#641)", async () => {
    getUnitExecution.mockResolvedValue({ runtime: "spring-voyage" });
    getAgentRuntimeModels.mockResolvedValue([]);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("unit-execution-card");
    expect(
      screen.getByTestId("execution-provider-select"),
    ).toBeInTheDocument();
    // Model renders as a free-text input because no Provider is
    // selected yet, so there's no catalog to drive a dropdown.
    expect(screen.getByTestId("execution-model-input")).toBeInTheDocument();
  });

  it("keeps the Model slot visible when agent=custom (always rendered post-#1702)", async () => {
    getUnitExecution.mockResolvedValue({ runtime: "custom" });
    getAgentRuntimeModels.mockResolvedValue([]);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("unit-execution-card");
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
    // Model is always rendered — a free-text input is shown when no
    // catalog is available for the chosen tool.
    expect(
      screen.getByTestId("execution-model-input"),
    ).toBeInTheDocument();
  });

  it("shows Model Provider and Model again when agent flips back to spring-voyage", async () => {
    getUnitExecution.mockResolvedValue({ runtime: "codex" });

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const toolSelect = (await screen.findByTestId(
      "execution-agent-runtime-select",
    )) as HTMLSelectElement;
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();

    fireEvent.change(toolSelect, { target: { value: "spring-voyage" } });
    await screen.findByTestId("execution-provider-select");
  });

  it("PUTs only the fields the operator declared on Save (no runtime, with agent)", async () => {
    getUnitExecution.mockResolvedValue({});
    setUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "claude-code",
    });

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const imageInput = (await screen.findByTestId(
      "execution-image-input",
    )) as HTMLInputElement;
    const toolSelect = screen.getByTestId(
      "execution-agent-runtime-select",
    ) as HTMLSelectElement;

    fireEvent.change(imageInput, {
      target: { value: "ghcr.io/acme/spring-agent:v1" },
    });
    fireEvent.change(toolSelect, { target: { value: "claude-code" } });

    fireEvent.click(screen.getByRole("button", { name: /^Save$/i }));

    await waitFor(() => {
      expect(setUnitExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setUnitExecution.mock.calls[0];
    expect(id).toBe("eng-team");
    expect(body?.image).toBe("ghcr.io/acme/spring-agent:v1");
    // ADR-0038 (PR-1b): the wire field renamed `agent` → `runtime`;
    // the legacy `kind` field was removed; `model` is now structured
    // `{provider, id}` (null when only one of the two is set).
    expect(body?.runtime).toBe("claude-code");
    expect((body as { agent?: string | null })?.agent).toBeUndefined();
    expect((body as { tool?: string | null })?.tool).toBeUndefined();
    expect((body as { provider?: string | null })?.provider).toBeUndefined();
    expect(body?.model).toBeNull();
  });

  it("per-field Clear re-PUTs with the remaining fields via the partial-update contract (#628)", async () => {
    // Initial state: image + agent set.
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "claude-code",
    });
    setUnitExecution.mockResolvedValue({ runtime: "claude-code" });

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const clearImageBtn = await screen.findByTestId("execution-clear-image");
    fireEvent.click(clearImageBtn);

    await waitFor(() => {
      expect(setUnitExecution).toHaveBeenCalledTimes(1);
    });
    const [, body] = setUnitExecution.mock.calls[0];
    // Image cleared, runtime carried through verbatim.
    expect(body?.image).toBeNull();
    expect(body?.runtime).toBe("claude-code");
  });

  it("DELETEs the execution block when the operator clears every field", async () => {
    // Only image set; clearing it should trigger the DELETE fall-through.
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
    });
    clearUnitExecution.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const clearImageBtn = await screen.findByTestId("execution-clear-image");
    fireEvent.click(clearImageBtn);

    await waitFor(() => {
      expect(clearUnitExecution).toHaveBeenCalledTimes(1);
    });
    expect(clearUnitExecution).toHaveBeenCalledWith("eng-team");
    // And no stale PUT fired.
    expect(setUnitExecution).not.toHaveBeenCalled();
  });

  it("renders axe-clean on the default (empty) state", async () => {
    getUnitExecution.mockResolvedValue({});

    const { container } = render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("unit-execution-card");
    await expectNoAxeViolations(container);
  });

  it("Clear all issues the dedicated DELETE verb", async () => {
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
    });
    clearUnitExecution.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const clearAllBtn = await screen.findByRole("button", {
      name: /Clear execution defaults/i,
    });
    fireEvent.click(clearAllBtn);

    await waitFor(() => {
      expect(clearUnitExecution).toHaveBeenCalledTimes(1);
    });
    expect(clearUnitExecution).toHaveBeenCalledWith("eng-team");
  });
});
