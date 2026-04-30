// Unit tests for the new-engagement form (#1455 / #1456).

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { NewEngagementForm } from "./new-engagement-form";
import type { ValidatedTenantTreeNode } from "@/lib/api/validate-tenant-tree";

// ── next/navigation stubs ────────────────────────────────────────────────
const pushMock = vi.fn();
let currentSearchParams = new URLSearchParams();

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: pushMock }),
  useSearchParams: () => currentSearchParams,
}));

vi.mock("next/link", () => ({
  default: ({ href, children }: { href: string; children: ReactNode }) => (
    <a href={href}>{children}</a>
  ),
}));

// ── data hooks ───────────────────────────────────────────────────────────
const useTenantTreeMock = vi.fn();

vi.mock("@/lib/api/queries", () => ({
  useTenantTree: () => useTenantTreeMock(),
}));

// ── api client ───────────────────────────────────────────────────────────
const sendMessageMock = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    sendMessage: (body: unknown) => sendMessageMock(body),
  },
  ApiError: class extends Error {},
}));

// ── toast ─────────────────────────────────────────────────────────────────
const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

// ── render harness ────────────────────────────────────────────────────────
function renderForm() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <NewEngagementForm />
    </QueryClientProvider>,
  );
}

// ── tree fixture ─────────────────────────────────────────────────────────
const tree: ValidatedTenantTreeNode = {
  id: "tenant://default",
  name: "default",
  kind: "Tenant",
  status: "running",
  children: [
    {
      id: "engineering",
      name: "Engineering",
      kind: "Unit",
      status: "stopped",
      children: [
        {
          id: "ada",
          name: "Ada Lovelace",
          kind: "Agent",
          status: "running",
          primaryParentId: "engineering",
        },
      ],
    },
    {
      id: "design",
      name: "Design",
      kind: "Unit",
      status: "stopped",
      children: [],
    },
  ],
};

beforeEach(() => {
  pushMock.mockClear();
  sendMessageMock.mockReset();
  toastMock.mockClear();
  currentSearchParams = new URLSearchParams();
  useTenantTreeMock.mockReset();
  useTenantTreeMock.mockReturnValue({
    data: tree,
    isPending: false,
    isError: false,
    error: null,
  });
});

describe("NewEngagementForm — picker", () => {
  it("renders every Unit and Agent in the tenant tree", () => {
    renderForm();
    expect(
      screen.getByTestId("engagement-new-pick-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-pick-unit-design"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-pick-agent-ada"),
    ).toBeInTheDocument();
  });

  it("filters by name and address", () => {
    renderForm();
    fireEvent.change(screen.getByTestId("engagement-new-filter"), {
      target: { value: "ada" },
    });
    expect(
      screen.getByTestId("engagement-new-pick-agent-ada"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("engagement-new-pick-unit-engineering"),
    ).toBeNull();
  });

  it("toggles a pick and shows it as a chip", () => {
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    // Click again to remove.
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    expect(
      screen.queryByTestId("engagement-new-chip-unit-engineering"),
    ).toBeNull();
  });
});

describe("NewEngagementForm — pre-population (#1456)", () => {
  it("seeds participants from `?participant=` query strings", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "unit://engineering");
    currentSearchParams.append("participant", "agent://ada");
    renderForm();
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("engagement-new-chip-agent-ada"),
    ).toBeInTheDocument();
  });

  it("ignores malformed `?participant=` values", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "garbage");
    currentSearchParams.append("participant", "unit://engineering");
    renderForm();
    expect(
      screen.getByTestId("engagement-new-chip-unit-engineering"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("engagement-new-selected"),
    ).toBeInTheDocument();
  });

  it("a seeded participant is removable before submit", () => {
    currentSearchParams = new URLSearchParams();
    currentSearchParams.append("participant", "unit://engineering");
    renderForm();
    fireEvent.click(
      screen.getByTestId("engagement-new-chip-remove-unit-engineering"),
    );
    expect(
      screen.queryByTestId("engagement-new-chip-unit-engineering"),
    ).toBeNull();
  });
});

describe("NewEngagementForm — submit", () => {
  it("blocks submit with an inline error when no participants are picked", async () => {
    renderForm();
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "hello" },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));
    const error = await screen.findByTestId("engagement-new-error");
    expect(error).toHaveTextContent(/at least one participant/i);
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("blocks submit with an inline error when the body is empty", async () => {
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.click(screen.getByTestId("engagement-new-submit"));
    const error = await screen.findByTestId("engagement-new-error");
    expect(error).toHaveTextContent(/first message/i);
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("sends the seed to the first participant and navigates to the new thread", async () => {
    sendMessageMock.mockResolvedValueOnce({
      threadId: "thread-1",
      messageId: "msg-1",
    });
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "Kick off the work." },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));

    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledTimes(1);
    });
    expect(sendMessageMock).toHaveBeenCalledWith({
      to: { scheme: "unit", path: "engineering" },
      type: "Domain",
      threadId: null,
      payload: "Kick off the work.",
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/engagement/thread-1");
    });
  });

  it("fans the seed out to additional participants under the same thread (1:M)", async () => {
    sendMessageMock
      .mockResolvedValueOnce({ threadId: "thread-7", messageId: "m-1" })
      .mockResolvedValueOnce({ threadId: "thread-7", messageId: "m-2" });
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.click(screen.getByTestId("engagement-new-pick-agent-ada"));
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "Multi-cast hello." },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));

    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledTimes(2);
    });
    // First call: seed to engineering with no threadId.
    expect(sendMessageMock).toHaveBeenNthCalledWith(1, {
      to: { scheme: "unit", path: "engineering" },
      type: "Domain",
      threadId: null,
      payload: "Multi-cast hello.",
    });
    // Second call: same threadId echoed to ada.
    expect(sendMessageMock).toHaveBeenNthCalledWith(2, {
      to: { scheme: "agent", path: "ada" },
      type: "Domain",
      threadId: "thread-7",
      payload: "Multi-cast hello.",
    });
    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/engagement/thread-7");
    });
  });

  it("surfaces a partial-fanout warning toast but still navigates on first-success", async () => {
    sendMessageMock
      .mockResolvedValueOnce({ threadId: "thread-9", messageId: "m-1" })
      .mockRejectedValueOnce(new Error("Permission denied for agent://ada"));
    renderForm();
    fireEvent.click(screen.getByTestId("engagement-new-pick-unit-engineering"));
    fireEvent.click(screen.getByTestId("engagement-new-pick-agent-ada"));
    fireEvent.change(screen.getByTestId("engagement-new-body"), {
      target: { value: "Try anyway." },
    });
    fireEvent.click(screen.getByTestId("engagement-new-submit"));

    await waitFor(() => {
      expect(pushMock).toHaveBeenCalledWith("/engagement/thread-9");
    });
    expect(
      toastMock.mock.calls.some(([arg]) =>
        /some participants did not receive/i.test(
          (arg as { title?: string })?.title ?? "",
        ),
      ),
    ).toBe(true);
  });
});
