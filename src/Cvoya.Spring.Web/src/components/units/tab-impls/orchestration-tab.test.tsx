import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  UnitOrchestrationResponse,
  UnitPolicyResponse,
} from "@/lib/api/types";

const getUnitPolicy = vi.fn<(id: string) => Promise<UnitPolicyResponse>>();
const setUnitPolicy =
  vi.fn<
    (id: string, p: UnitPolicyResponse | null) => Promise<UnitPolicyResponse>
  >();
const getUnitOrchestration =
  vi.fn<(id: string) => Promise<UnitOrchestrationResponse>>();
const setUnitOrchestration =
  vi.fn<
    (
      id: string,
      body: UnitOrchestrationResponse,
    ) => Promise<UnitOrchestrationResponse>
  >();
const clearUnitOrchestration = vi.fn<(id: string) => Promise<void>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitPolicy: (id: string) => getUnitPolicy(id),
    setUnitPolicy: (id: string, p: UnitPolicyResponse | null) =>
      setUnitPolicy(id, p),
    getUnitOrchestration: (id: string) => getUnitOrchestration(id),
    setUnitOrchestration: (id: string, body: UnitOrchestrationResponse) =>
      setUnitOrchestration(id, body),
    clearUnitOrchestration: (id: string) => clearUnitOrchestration(id),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { OrchestrationTab } from "./orchestration-tab";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0, staleTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

describe("OrchestrationTab", () => {
  beforeEach(() => {
    getUnitPolicy.mockReset();
    setUnitPolicy.mockReset();
    getUnitOrchestration.mockReset();
    setUnitOrchestration.mockReset();
    clearUnitOrchestration.mockReset();
    toastMock.mockReset();
  });

  it("offers every platform-offered strategy key plus an unset / inferred option", async () => {
    getUnitPolicy.mockResolvedValue({});
    getUnitOrchestration.mockResolvedValue({ strategy: null });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const select = (await screen.findByTestId(
      "orchestration-strategy-select",
    )) as HTMLSelectElement;
    // First option is the inferred / default sentinel; the next three are
    // the platform-registered keys per ADR-0010.
    expect(
      Array.from(select.options).map((o) => o.value),
    ).toEqual(["__unset__", "ai", "workflow", "label-routed"]);
    // Editable now that the dedicated /orchestration endpoint exists (#606).
    expect(select).not.toBeDisabled();
  });

  it("reports the default strategy when no manifest key and no label routing policy are set", async () => {
    getUnitPolicy.mockResolvedValue({});
    getUnitOrchestration.mockResolvedValue({ strategy: null });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const effective = await screen.findByTestId("orchestration-effective");
    expect(effective.textContent).toContain("ai");
    expect(effective.textContent).toContain("platform default");
  });

  it("reports label-routed inferred when a LabelRouting policy is set", async () => {
    getUnitPolicy.mockResolvedValue({
      labelRouting: {
        triggerLabels: { frontend: "frontend-engineer" },
        addOnAssign: null,
        removeOnAssign: null,
      },
    });
    getUnitOrchestration.mockResolvedValue({ strategy: null });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const effective = await screen.findByTestId("orchestration-effective");
    expect(effective.textContent).toContain("label-routed");
    expect(effective.textContent).toContain("policy inference");
  });

  it("reports the manifest-declared strategy when the /orchestration endpoint surfaces a key", async () => {
    getUnitPolicy.mockResolvedValue({});
    getUnitOrchestration.mockResolvedValue({ strategy: "workflow" });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const effective = await screen.findByTestId("orchestration-effective");
    expect(effective.textContent).toContain("workflow");
    expect(effective.textContent).toContain("manifest key");
  });

  it("PUTs the selected strategy when the dropdown changes", async () => {
    getUnitPolicy.mockResolvedValue({});
    getUnitOrchestration.mockResolvedValue({ strategy: null });
    setUnitOrchestration.mockResolvedValue({ strategy: "workflow" });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const select = (await screen.findByTestId(
      "orchestration-strategy-select",
    )) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "workflow" } });

    await waitFor(() => {
      expect(setUnitOrchestration).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setUnitOrchestration.mock.calls[0];
    expect(id).toBe("eng-team");
    expect(body?.strategy).toBe("workflow");
  });

  it("DELETEs the orchestration slot when the dropdown resets to the inferred / default sentinel", async () => {
    getUnitPolicy.mockResolvedValue({});
    getUnitOrchestration.mockResolvedValue({ strategy: "workflow" });
    clearUnitOrchestration.mockResolvedValue(undefined);

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const select = (await screen.findByTestId(
      "orchestration-strategy-select",
    )) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: "__unset__" } });

    await waitFor(() => {
      expect(clearUnitOrchestration).toHaveBeenCalledTimes(1);
    });
    expect(clearUnitOrchestration).toHaveBeenCalledWith("eng-team");
  });

  it("renders existing label-routing rules from the server", async () => {
    getUnitPolicy.mockResolvedValue({
      labelRouting: {
        triggerLabels: { backend: "backend-engineer" },
        addOnAssign: ["in-progress"],
        removeOnAssign: ["needs-triage"],
      },
    });
    getUnitOrchestration.mockResolvedValue({ strategy: null });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    await screen.findByTestId("label-routing-rules");
    const inputs = screen.getAllByRole("textbox") as HTMLInputElement[];
    const values = inputs.map((i) => i.value);
    expect(values).toContain("backend");
    expect(values).toContain("backend-engineer");
    expect(values).toContain("in-progress");
    expect(values).toContain("needs-triage");
  });

  it("PUTs the full policy with the new trigger label on save", async () => {
    getUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github"], blocked: null },
    });
    getUnitOrchestration.mockResolvedValue({ strategy: null });
    setUnitPolicy.mockResolvedValue({
      skill: { allowed: ["github"], blocked: null },
      labelRouting: {
        triggerLabels: { frontend: "frontend-engineer" },
        addOnAssign: null,
        removeOnAssign: null,
      },
    });

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const labelInput = (await screen.findByTestId(
      "label-routing-new-label",
    )) as HTMLInputElement;
    const targetInput = (await screen.findByTestId(
      "label-routing-new-target",
    )) as HTMLInputElement;
    fireEvent.change(labelInput, { target: { value: "frontend" } });
    fireEvent.change(targetInput, {
      target: { value: "frontend-engineer" },
    });
    fireEvent.click(screen.getByRole("button", { name: /^Add$/i }));
    fireEvent.click(
      screen.getByRole("button", { name: /^Save label routing$/i }),
    );

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledTimes(1);
    });
    const [id, body] = setUnitPolicy.mock.calls[0];
    expect(id).toBe("eng-team");
    // Carries the existing Skill dimension through verbatim.
    expect(body?.skill).toEqual({ allowed: ["github"], blocked: null });
    // And writes the new label-routing shape.
    expect(body?.labelRouting?.triggerLabels).toEqual({
      frontend: "frontend-engineer",
    });
  });

  it("clears the dimension when the Clear button fires", async () => {
    getUnitPolicy.mockResolvedValue({
      labelRouting: {
        triggerLabels: { frontend: "frontend-engineer" },
        addOnAssign: null,
        removeOnAssign: null,
      },
    });
    getUnitOrchestration.mockResolvedValue({ strategy: null });
    setUnitPolicy.mockResolvedValue({});

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const clearBtn = await screen.findByRole("button", {
      name: /Clear label routing policy/i,
    });
    fireEvent.click(clearBtn);

    await waitFor(() => {
      expect(setUnitPolicy).toHaveBeenCalledTimes(1);
    });
    const [, body] = setUnitPolicy.mock.calls[0];
    expect(body?.labelRouting).toBeNull();
  });
});
