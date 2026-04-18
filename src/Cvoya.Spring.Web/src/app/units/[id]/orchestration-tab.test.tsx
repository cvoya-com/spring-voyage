import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { UnitPolicyResponse } from "@/lib/api/types";

const getUnitPolicy = vi.fn<(id: string) => Promise<UnitPolicyResponse>>();
const setUnitPolicy =
  vi.fn<
    (id: string, p: UnitPolicyResponse | null) => Promise<UnitPolicyResponse>
  >();

vi.mock("@/lib/api/client", () => ({
  api: {
    getUnitPolicy: (id: string) => getUnitPolicy(id),
    setUnitPolicy: (id: string, p: UnitPolicyResponse | null) =>
      setUnitPolicy(id, p),
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
    toastMock.mockReset();
  });

  it("offers every platform-offered strategy key and renders the selector read-only", async () => {
    getUnitPolicy.mockResolvedValue({});

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const select = (await screen.findByTestId(
      "orchestration-strategy-select",
    )) as HTMLSelectElement;
    // Dropdown offers the three platform-registered keys — ADR-0010.
    expect(
      Array.from(select.options).map((o) => o.value),
    ).toEqual(["ai", "workflow", "label-routed"]);
    // Read-only until the /orchestration endpoint lands (#606).
    expect(select).toBeDisabled();
  });

  it("reports the default strategy when no manifest key and no label routing policy are set", async () => {
    getUnitPolicy.mockResolvedValue({});

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

    render(
      <Wrapper>
        <OrchestrationTab unitId="eng-team" />
      </Wrapper>,
    );

    const effective = await screen.findByTestId("orchestration-effective");
    expect(effective.textContent).toContain("label-routed");
    expect(effective.textContent).toContain("policy inference");
  });

  it("renders existing label-routing rules from the server", async () => {
    getUnitPolicy.mockResolvedValue({
      labelRouting: {
        triggerLabels: { backend: "backend-engineer" },
        addOnAssign: ["in-progress"],
        removeOnAssign: ["needs-triage"],
      },
    });

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
