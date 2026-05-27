// Tests for the shared <MessageComposer> primitive (#1574 / #1575).
//
// Wraps the engagement-portal compact composer (textarea + side-by-side
// Send button + ⌘/Ctrl+Enter tooltip from #1553) and serves the inbox
// reply flow + the unit/agent Messages tab as well. The engagement-
// composer wrapper has its own thinner test; these tests pin the
// primitive's contract directly.

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const sendThreadMessageMock = vi.fn();
const sendMessageMock = vi.fn();
const listCallerHumansMock = vi.fn(async () => [] as unknown[]);
vi.mock("@/lib/api/client", async () => {
  const actual =
    await vi.importActual<typeof import("@/lib/api/client")>("@/lib/api/client");
  return {
    ...actual,
    api: {
      sendThreadMessage: (id: string, body: unknown) =>
        sendThreadMessageMock(id, body),
      sendMessage: (body: unknown) => sendMessageMock(body),
      listCallerHumans: () => listCallerHumansMock(),
    },
  };
});

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { MessageComposer } from "./message-composer";

function wrap(node: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
  return <QueryClientProvider client={client}>{node}</QueryClientProvider>;
}

beforeEach(() => {
  sendThreadMessageMock.mockReset();
  sendMessageMock.mockReset();
  toastMock.mockReset();
  listCallerHumansMock.mockReset();
  // Default: no bound Hats so the selector renders nothing and the
  // existing tests pin the legacy composer chrome. Tests that exercise
  // the from-selector replace this default in their own beforeEach.
  listCallerHumansMock.mockResolvedValue([]);
});

describe("MessageComposer — initial render", () => {
  it("renders the form with the default information mode", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const form = screen.getByTestId("message-composer");
    expect(form).toHaveAttribute("data-kind", "information");
    expect(form).toHaveAttribute("aria-label", "Send message");
  });

  it("uses the supplied `testId` so consumers can scope queries", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          testId="my-composer"
        />,
      ),
    );
    expect(screen.getByTestId("my-composer")).toBeInTheDocument();
    expect(screen.getByTestId("my-composer-input")).toBeInTheDocument();
    expect(screen.getByTestId("my-composer-send")).toBeInTheDocument();
  });

  it("derives the placeholder from the recipient address", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    expect(screen.getByTestId("message-composer-input")).toHaveAttribute(
      "placeholder",
      "Message agent://ada…",
    );
  });

  it("exposes the ⌘/Ctrl+Enter shortcut on the Send button (tooltip + aria-label)", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const send = screen.getByTestId("message-composer-send");
    expect(send).toHaveAttribute("title", "⌘/Ctrl+Enter to send");
    expect(send).toHaveAttribute(
      "aria-label",
      "Send message (⌘/Ctrl+Enter)",
    );
    // The hint is no longer rendered as inline body text.
    expect(screen.queryByText("⌘/Ctrl+Enter to send")).not.toBeInTheDocument();
  });
});

describe("MessageComposer — disabled / missing recipient", () => {
  it("disables the textarea and Send button when recipient is null", () => {
    render(wrap(<MessageComposer threadId="t-1" recipient={null} />));
    expect(screen.getByTestId("message-composer-input")).toBeDisabled();
    expect(screen.getByTestId("message-composer-send")).toBeDisabled();
  });

  it("disables Send while the textarea is empty", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    expect(screen.getByTestId("message-composer-send")).toBeDisabled();
  });

  it("enables Send once non-whitespace text is entered", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "Hello" },
    });
    expect(screen.getByTestId("message-composer-send")).not.toBeDisabled();
  });
});

describe("MessageComposer — send routing", () => {
  it("posts to the existing thread via sendThreadMessage", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "Reply." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledWith("t-1", {
        to: { scheme: "agent", path: "ada" },
        text: "Reply.",
      });
    });
    expect(sendMessageMock).not.toHaveBeenCalled();
  });

  it("creates a fresh thread via sendMessage when threadId is null", async () => {
    sendMessageMock.mockResolvedValue({ threadId: "t-new" });
    render(
      wrap(
        <MessageComposer
          threadId={null}
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "First message." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledWith({
        to: { scheme: "agent", path: "ada" },
        type: "Domain",
        threadId: null,
        payload: "First message.",
      });
    });
    expect(sendThreadMessageMock).not.toHaveBeenCalled();
  });

  it("submits via Cmd+Enter from the textarea", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const input = screen.getByTestId("message-composer-input");
    fireEvent.change(input, { target: { value: "Shortcut send." } });
    fireEvent.keyDown(input, { key: "Enter", metaKey: true });
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledTimes(1);
    });
  });

  it("submits via Ctrl+Enter from the textarea", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const input = screen.getByTestId("message-composer-input");
    fireEvent.change(input, { target: { value: "Shortcut send." } });
    fireEvent.keyDown(input, { key: "Enter", ctrlKey: true });
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledTimes(1);
    });
  });

  it("clears the textarea on success", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const input = screen.getByTestId(
      "message-composer-input",
    ) as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: "Reply." } });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(input.value).toBe("");
    });
  });

  it("calls onSendSuccess after a successful send", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    const onSendSuccess = vi.fn();
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          onSendSuccess={onSendSuccess}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "Reply." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(onSendSuccess).toHaveBeenCalledTimes(1);
    });
  });

  it("toasts an error when the send fails", async () => {
    sendThreadMessageMock.mockRejectedValue(new Error("server-error"));
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "Reply." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Failed to send message",
          // Non-ApiError values render through `formatTranslatedError`'s
          // generic fallback (#2163); the "API error N" prose is gone.
          description: "Something went wrong.",
          variant: "destructive",
        }),
      );
    });
  });

  it("toasts the translated copy when the send throws ProblemDetails (#2163)", async () => {
    const { ApiError } = await import("@/lib/api/client");
    sendThreadMessageMock.mockRejectedValue(
      new ApiError(404, "Not Found", {
        type: "https://cvoya.com/problems/agent-not-found",
        title: "Not Found",
        status: 404,
        detail: "AgentNotFound: ada is gone.",
        code: "AgentNotFound",
        traceId: "00-composer",
      }),
    );
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "Reply." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(toastMock).toHaveBeenCalledWith(
        expect.objectContaining({
          title: "Failed to send message",
          description:
            "Agent not found. It may have been deleted. Refresh the page or pick another agent.",
          variant: "destructive",
        }),
      );
    });
  });
});

describe("MessageComposer — answer mode", () => {
  it("shows the answer banner and updates the Send label", () => {
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          kind="answer"
        />,
      ),
    );
    expect(screen.getByText("Answering a question")).toBeInTheDocument();
    expect(screen.getByTestId("message-composer")).toHaveAttribute(
      "aria-label",
      "Answer clarifying question",
    );
    expect(
      screen.getByRole("button", { name: /^send answer/i }),
    ).toBeInTheDocument();
  });

  it("attaches the kind field on the wire only in answer mode", async () => {
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          kind="answer"
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "On main." },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalledWith("t-1", {
        to: { scheme: "agent", path: "ada" },
        text: "On main.",
        kind: "answer",
      });
    });
  });

  it("calls onKindChange when the 'Send as regular message' escape hatch is clicked", () => {
    const onKindChange = vi.fn();
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          kind="answer"
          onKindChange={onKindChange}
        />,
      ),
    );
    fireEvent.click(
      screen.getByRole("button", {
        name: /switch to regular message mode/i,
      }),
    );
    expect(onKindChange).toHaveBeenCalledWith("information");
  });
});

// ---------------------------------------------------------------------------
// ADR-0062 § 5 — Hat from-selector wiring.
// ---------------------------------------------------------------------------

describe("MessageComposer — from-selector (ADR-0062)", () => {
  const PRIMARY = {
    humanId: "00000000-0000-0000-0000-000000000001",
    displayName: "Bob",
    isPrimary: true,
    memberships: [
      {
        unitId: "00000000-0000-0000-0000-0000000000a0",
        unitDisplayName: "Magazine",
        roles: ["designer"],
      },
    ],
  };
  const SECONDARY = {
    humanId: "00000000-0000-0000-0000-000000000002",
    displayName: "Alice",
    isPrimary: false,
    memberships: [
      {
        unitId: "00000000-0000-0000-0000-0000000000a1",
        unitDisplayName: "Editorial",
        roles: ["reviewer"],
      },
    ],
  };

  it("hides the selector when the caller has no bound Hats", async () => {
    listCallerHumansMock.mockResolvedValue([]);
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    // Give react-query a tick to settle.
    await waitFor(() => {
      expect(listCallerHumansMock).toHaveBeenCalled();
    });
    expect(
      screen.queryByTestId("message-composer-from"),
    ).not.toBeInTheDocument();
  });

  it("collapses to a static badge with a single bound Hat", async () => {
    listCallerHumansMock.mockResolvedValue([PRIMARY]);
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const selector = await screen.findByTestId("message-composer-from");
    expect(selector).toHaveAttribute("data-mode", "static");
    expect(selector).toHaveTextContent("Bob (designer in Magazine)");
  });

  it("forwards the selected Hat as the explicit `from` field on a thread send", async () => {
    listCallerHumansMock.mockResolvedValue([PRIMARY, SECONDARY]);
    sendThreadMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    const select = (await screen.findByTestId(
      "message-composer-from-select",
    )) as HTMLSelectElement;
    // Default lands on the primary Hat per ADR-0062 § 5.
    await waitFor(() => {
      expect(select.value).toBe(PRIMARY.humanId);
    });

    // Switch to Alice and send.
    fireEvent.change(select, { target: { value: SECONDARY.humanId } });
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "ping" },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));

    await waitFor(() => {
      expect(sendThreadMessageMock).toHaveBeenCalled();
    });
    const [, body] = sendThreadMessageMock.mock.calls[0];
    expect(body).toMatchObject({
      to: { scheme: "agent", path: "ada" },
      text: "ping",
      from: SECONDARY.humanId,
    });
  });

  it("forwards the selected Hat as `from` on a new-thread send", async () => {
    listCallerHumansMock.mockResolvedValue([PRIMARY, SECONDARY]);
    sendMessageMock.mockResolvedValue({});
    render(
      wrap(
        <MessageComposer
          threadId={null}
          recipient={{ scheme: "agent", path: "ada" }}
        />,
      ),
    );
    await screen.findByTestId("message-composer-from-select");
    fireEvent.change(screen.getByTestId("message-composer-input"), {
      target: { value: "hi" },
    });
    fireEvent.click(screen.getByTestId("message-composer-send"));
    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalled();
    });
    const [body] = sendMessageMock.mock.calls[0];
    expect(body).toMatchObject({
      to: { scheme: "agent", path: "ada" },
      type: "Domain",
      threadId: null,
      payload: "hi",
      from: PRIMARY.humanId,
    });
  });

  it("seeds the default selection from `defaultHumanId` when supplied (reply pin)", async () => {
    listCallerHumansMock.mockResolvedValue([PRIMARY, SECONDARY]);
    render(
      wrap(
        <MessageComposer
          threadId="t-1"
          recipient={{ scheme: "agent", path: "ada" }}
          defaultHumanId={SECONDARY.humanId}
        />,
      ),
    );
    const select = (await screen.findByTestId(
      "message-composer-from-select",
    )) as HTMLSelectElement;
    await waitFor(() => {
      expect(select.value).toBe(SECONDARY.humanId);
    });
  });
});
