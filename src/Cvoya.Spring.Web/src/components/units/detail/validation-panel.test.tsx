/**
 * Tests for `ValidationPanel` (T-07, issue #949).
 *
 * Covers the four branches the panel renders:
 *   - Validating: step checklist tracks the most recent
 *     `ValidationProgress` event (`VerifyingTool` → second step is the
 *     spinner; first step is done; later steps muted).
 *   - Error: structured block with friendly copy + Retry + Edit
 *     credential actions. Retry fires revalidate; Edit credential runs
 *     update-then-revalidate in order.
 *   - Stopped: "Validation passed" summary + Revalidate button.
 *   - Running: panel returns null.
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  ActivityEvent,
  UnitResponse,
  UnitValidationError,
} from "@/lib/api/types";

// --- Mocks ------------------------------------------------------------

const revalidateUnitMock = vi.fn();
const createUnitSecretMock = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    revalidateUnit: (id: string) => revalidateUnitMock(id),
    createUnitSecret: (id: string, body: unknown) =>
      createUnitSecretMock(id, body),
  },
}));

// The panel consumes `useActivityStream` to track validation progress.
// We capture the filter the panel registers so the Validating test can
// drive a progress event through it synchronously.
let capturedFilters: Array<(e: ActivityEvent) => boolean> = [];
vi.mock("@/lib/stream/use-activity-stream", () => ({
  useActivityStream: (options: { filter?: (e: ActivityEvent) => boolean }) => {
    if (options?.filter) capturedFilters.push(options.filter);
    return { events: [], connected: true };
  },
}));

import ValidationPanel from "./validation-panel";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

function makeUnit(overrides: Partial<UnitResponse>): UnitResponse {
  // #1738: `unit.tool` was retired in #1732. Tests that need the
  // execution-block context for runtime resolution pass `agent` /
  // `provider` / `kind` directly to <ValidationPanel> as props.
  return {
    id: "alpha-id",
    name: "alpha",
    displayName: "Alpha",
    description: "",
    registeredAt: "2026-04-21T00:00:00Z",
    status: "Validating",
    model: "claude-sonnet-4.7",
    color: null,
    provider: null,
    hosting: null,
    lastValidationError: null,
    lastValidationRunId: null,
    ...overrides,
  } as UnitResponse;
}

function makeEvent(overrides: Partial<ActivityEvent>): ActivityEvent {
  return {
    id: "evt-1",
    timestamp: "2026-04-21T12:00:00Z",
    source: { scheme: "unit", path: "alpha-id" },
    eventType: "ValidationProgress",
    severity: "Info",
    summary: "progress",
    ...overrides,
  } as ActivityEvent;
}

beforeEach(() => {
  capturedFilters = [];
  revalidateUnitMock.mockReset();
  createUnitSecretMock.mockReset();
});

// ---------------------------------------------------------------------
// Validating — step checklist
// ---------------------------------------------------------------------

describe("ValidationPanel — Validating status", () => {
  it("advances the checklist as ValidationProgress events arrive", () => {
    const unit = makeUnit({ status: "Validating" });
    const { rerender } = render(
      wrap(<ValidationPanel unit={unit} image="ghcr.io/img:1" runtime="docker" />),
    );

    // Before any event: first step (PullingImage) is active.
    const list = screen.getByTestId("validation-step-checklist");
    expect(
      list.querySelector('[data-step="PullingImage"]')?.getAttribute("data-state"),
    ).toBe("active");

    // Simulate a ValidationProgress event announcing VerifyingTool. The
    // mocked hook stashed the filter — drive the event through it (the
    // real filter has the side-effect of calling setLiveStep). Filters
    // captured include UnitDetailClient-style callers, so find the one
    // that returns false (the panel's step tracker) and call it.
    act(() => {
      for (const filter of capturedFilters) {
        filter(
          makeEvent({
            source: { scheme: "unit", path: "alpha-id" },
            eventType: "ValidationProgress",
            details: { step: "VerifyingTool", status: "Running" },
          }),
        );
      }
    });

    // Re-render so the state update takes effect in the DOM.
    rerender(
      wrap(<ValidationPanel unit={unit} image="ghcr.io/img:1" runtime="docker" />),
    );

    const list2 = screen.getByTestId("validation-step-checklist");
    expect(
      list2.querySelector('[data-step="PullingImage"]')?.getAttribute("data-state"),
    ).toBe("done");
    expect(
      list2.querySelector('[data-step="VerifyingTool"]')?.getAttribute("data-state"),
    ).toBe("active");
    expect(
      list2.querySelector('[data-step="ValidatingCredential"]')?.getAttribute(
        "data-state",
      ),
    ).toBe("future");
    expect(
      list2.querySelector('[data-step="ResolvingModel"]')?.getAttribute("data-state"),
    ).toBe("future");
  });
});

// ---------------------------------------------------------------------
// Error — friendly copy + actions
// ---------------------------------------------------------------------

describe("ValidationPanel — Error status", () => {
  it("renders CredentialInvalid with the friendly copy and run id", () => {
    const err: UnitValidationError = {
      step: "ValidatingCredential",
      code: "CredentialInvalid",
      message: "Server-supplied raw message (hidden).",
      details: null,
    };
    const unit = makeUnit({
      status: "Error",
      lastValidationError: err,
      lastValidationRunId: "wf-abc-123",
    });

    render(wrap(<ValidationPanel unit={unit} image="img" runtime="docker" />));

    expect(screen.getByTestId("validation-panel")).toHaveAttribute(
      "data-panel-state",
      "error",
    );
    // Friendly copy — not the raw server message.
    const copy = screen.getByTestId("validation-panel-error-copy").textContent ?? "";
    expect(copy).toMatch(/credential was rejected by the runtime/i);
    expect(copy).not.toMatch(/hidden/i);
    // Run id rendered.
    expect(screen.getByTestId("validation-panel-run-id")).toHaveTextContent(
      "wf-abc-123",
    );
  });

  it("Retry validation triggers revalidate once", async () => {
    const err: UnitValidationError = {
      step: "ValidatingCredential",
      code: "CredentialInvalid",
      message: "rejected",
      details: null,
    };
    const unit = makeUnit({
      status: "Error",
      lastValidationError: err,
    });
    revalidateUnitMock.mockResolvedValue(undefined);

    render(wrap(<ValidationPanel unit={unit} />));

    const retry = screen.getByTestId("validation-panel-retry");
    await act(async () => {
      fireEvent.click(retry);
    });

    await waitFor(() => {
      expect(revalidateUnitMock).toHaveBeenCalledTimes(1);
    });
    expect(revalidateUnitMock).toHaveBeenCalledWith("alpha");
  });

  it("Edit credential → Save fires createUnitSecret then revalidate in order", async () => {
    const err: UnitValidationError = {
      step: "ValidatingCredential",
      code: "CredentialInvalid",
      message: "rejected",
      details: null,
    };
    const unit = makeUnit({
      status: "Error",
      lastValidationError: err,
    });

    // Record the call order across both mocks.
    const callOrder: string[] = [];
    createUnitSecretMock.mockImplementation(async () => {
      callOrder.push("createUnitSecret");
    });
    revalidateUnitMock.mockImplementation(async () => {
      callOrder.push("revalidateUnit");
    });

    // #1738: pass the runtime registry id via the `agent` prop —
    // `resolveRuntimeId` reads it directly (not from `unit.tool`).
    render(wrap(<ValidationPanel unit={unit} agent="claude-code" />));

    // Open the inline editor.
    const edit = screen.getByTestId("validation-panel-edit-credential");
    await act(async () => {
      fireEvent.click(edit);
    });

    const input = screen.getByTestId(
      "validation-panel-credential-input",
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.change(input, { target: { value: "sk-ant-new" } });
    });

    const save = screen.getByTestId("validation-panel-credential-save");
    await act(async () => {
      fireEvent.click(save);
    });

    await waitFor(() => {
      expect(createUnitSecretMock).toHaveBeenCalledTimes(1);
      expect(revalidateUnitMock).toHaveBeenCalledTimes(1);
    });
    // claude-code → claude runtime → `anthropic-api-key` secret per
    // `getRuntimeSecretName`.
    expect(createUnitSecretMock).toHaveBeenCalledWith("alpha", {
      name: "anthropic-api-key",
      value: "sk-ant-new",
    });
    // Order matters — secret write first, then revalidate.
    expect(callOrder).toEqual(["createUnitSecret", "revalidateUnit"]);
  });
});

// ---------------------------------------------------------------------
// Stopped — revalidate CTA
// ---------------------------------------------------------------------

describe("ValidationPanel — Stopped status", () => {
  it('renders "Validation passed" + a Revalidate button that fires revalidate', async () => {
    const unit = makeUnit({ status: "Stopped" });
    revalidateUnitMock.mockResolvedValue(undefined);

    render(wrap(<ValidationPanel unit={unit} />));

    expect(screen.getByTestId("validation-panel")).toHaveAttribute(
      "data-panel-state",
      "stopped",
    );
    expect(screen.getByText(/last validation succeeded/i)).toBeInTheDocument();

    const btn = screen.getByTestId("validation-panel-revalidate");
    await act(async () => {
      fireEvent.click(btn);
    });

    await waitFor(() => {
      expect(revalidateUnitMock).toHaveBeenCalledTimes(1);
    });
    expect(revalidateUnitMock).toHaveBeenCalledWith("alpha");
  });
});

// ---------------------------------------------------------------------
// Hidden states
// ---------------------------------------------------------------------

describe("ValidationPanel — hidden states", () => {
  it("returns null for Running", () => {
    const unit = makeUnit({ status: "Running" });
    const { container } = render(wrap(<ValidationPanel unit={unit} />));
    expect(container.querySelector('[data-testid="validation-panel"]')).toBeNull();
  });

  it("returns null for Draft", () => {
    const unit = makeUnit({ status: "Draft" });
    const { container } = render(wrap(<ValidationPanel unit={unit} />));
    expect(container.querySelector('[data-testid="validation-panel"]')).toBeNull();
  });

  it("returns null for Starting / Stopping", () => {
    const a = render(wrap(<ValidationPanel unit={makeUnit({ status: "Starting" })} />));
    expect(a.container.querySelector('[data-testid="validation-panel"]')).toBeNull();
    a.unmount();
    const b = render(wrap(<ValidationPanel unit={makeUnit({ status: "Stopping" })} />));
    expect(b.container.querySelector('[data-testid="validation-panel"]')).toBeNull();
  });
});
