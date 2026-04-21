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

  it("renders all five execution fields with tool defaulting to unset (Provider + Model visible)", async () => {
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
    expect(
      screen.getByTestId("execution-runtime-select"),
    ).toBeInTheDocument();
    expect(screen.getByTestId("execution-tool-select")).toBeInTheDocument();
    // tool unset → provider + model slots visible.
    expect(
      screen.getByTestId("execution-provider-select"),
    ).toBeInTheDocument();
    // Model starts as a plain input because no provider is selected yet.
    expect(screen.getByTestId("execution-model-input")).toBeInTheDocument();
  });

  it("hides Provider but keeps Model visible when tool is claude-code (#641)", async () => {
    // #641 (PR #645 on wizard/agent; this issue is the unit tab parity):
    // Provider stays hidden for non-dapr-agent launchers, but the Model
    // dropdown is now rendered against the tool's catalog so the operator
    // can still pick a model family (e.g. claude-opus-4 for Claude Code).
    getUnitExecution.mockResolvedValue({ tool: "claude-code" });
    getAgentRuntimeModels.mockResolvedValue([
      { id: "claude-sonnet-4-6", displayName: "claude-sonnet-4-6", contextWindow: null },
      { id: "claude-opus-4-7", displayName: "claude-opus-4-7", contextWindow: null },
    ]);

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("execution-tool-select");
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("renders a Model dropdown populated from the tool's catalog when tool=codex (#641)", async () => {
    getUnitExecution.mockResolvedValue({ tool: "codex" });
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
    // Provider stays hidden — the tool implies it.
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();
  });

  it("shows both Provider and Model when tool=dapr-agent (#641)", async () => {
    getUnitExecution.mockResolvedValue({ tool: "dapr-agent" });
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

  it("omits the Model slot when tool=custom (no known catalog) (#641)", async () => {
    getUnitExecution.mockResolvedValue({ tool: "custom" });
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
    // Neither a Model select nor a free-text Model input is rendered.
    expect(
      screen.queryByTestId("execution-model-select"),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByTestId("execution-model-input"),
    ).not.toBeInTheDocument();
  });

  it("shows Provider and Model again when tool flips back to dapr-agent", async () => {
    getUnitExecution.mockResolvedValue({ tool: "codex" });

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const toolSelect = (await screen.findByTestId(
      "execution-tool-select",
    )) as HTMLSelectElement;
    expect(
      screen.queryByTestId("execution-provider-select"),
    ).not.toBeInTheDocument();

    fireEvent.change(toolSelect, { target: { value: "dapr-agent" } });
    await screen.findByTestId("execution-provider-select");
  });

  it("PUTs only the fields the operator declared on Save", async () => {
    getUnitExecution.mockResolvedValue({});
    setUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "podman",
    });

    render(
      <Wrapper>
        <ExecutionTab unitId="eng-team" />
      </Wrapper>,
    );

    const imageInput = (await screen.findByTestId(
      "execution-image-input",
    )) as HTMLInputElement;
    const runtimeSelect = screen.getByTestId(
      "execution-runtime-select",
    ) as HTMLSelectElement;

    fireEvent.change(imageInput, {
      target: { value: "ghcr.io/acme/spring-agent:v1" },
    });
    fireEvent.change(runtimeSelect, { target: { value: "podman" } });

    fireEvent.click(screen.getByRole("button", { name: /^Save$/i }));

    await waitFor(() => {
      expect(setUnitExecution).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setUnitExecution.mock.calls[0];
    expect(id).toBe("eng-team");
    expect(body?.image).toBe("ghcr.io/acme/spring-agent:v1");
    expect(body?.runtime).toBe("podman");
    expect(body?.tool).toBeNull();
    expect(body?.provider).toBeNull();
    expect(body?.model).toBeNull();
  });

  it("per-field Clear re-PUTs with the remaining fields via the partial-update contract (#628)", async () => {
    // Initial state: image + runtime set.
    getUnitExecution.mockResolvedValue({
      image: "ghcr.io/acme/spring-agent:v1",
      runtime: "docker",
    });
    setUnitExecution.mockResolvedValue({ runtime: "docker" });

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
    expect(body?.runtime).toBe("docker");
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
