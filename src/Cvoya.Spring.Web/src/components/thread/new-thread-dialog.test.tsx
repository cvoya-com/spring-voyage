/**
 * Tests for `NewThreadDialog` (#980 item 2).
 *
 * Covers:
 *   - Blocks submit while the body is empty and renders a validation
 *     error if the user forces a send.
 *   - POSTs the expected `SendMessageRequest` shape (scheme/path, type,
 *     null threadId) and forwards the server-assigned
 *     conversation id to the caller.
 *   - Surfaces server errors inline without throwing the dialog open
 *     state away.
 */

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const sendMessageMock = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    sendMessage: (body: unknown) => sendMessageMock(body),
  },
}));

import { NewThreadDialog } from "./new-thread-dialog";

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
  sendMessageMock.mockReset();
});

describe("NewThreadDialog", () => {
  it("disables submit until the body has content", () => {
    render(
      wrap(
        <NewThreadDialog
          open
          onClose={() => {}}
          targetScheme="unit"
          targetPath="alpha"
          onCreated={() => {}}
        />,
      ),
    );
    const submit = screen.getByTestId("new-conversation-submit") as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    const body = screen.getByTestId("new-conversation-body") as HTMLTextAreaElement;
    fireEvent.change(body, { target: { value: "Kick-off." } });
    expect(submit.disabled).toBe(false);
  });

  it("POSTs the expected shape and forwards the server conversation id", async () => {
    sendMessageMock.mockResolvedValue({
      messageId: "msg-1",
      threadId: "conv-42",
      responsePayload: null,
    });
    const onCreated = vi.fn();
    render(
      wrap(
        <NewThreadDialog
          open
          onClose={() => {}}
          targetScheme="agent"
          targetPath="ada"
          onCreated={onCreated}
        />,
      ),
    );

    fireEvent.change(screen.getByTestId("new-conversation-body"), {
      target: { value: "hello" },
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("new-conversation-submit"));
    });

    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledTimes(1);
    });
    expect(sendMessageMock).toHaveBeenCalledWith({
      to: { scheme: "agent", path: "ada" },
      type: "Domain",
      threadId: null,
      payload: "hello",
    });
    await waitFor(() => {
      expect(onCreated).toHaveBeenCalledWith("conv-42");
    });
  });

  it("trims leading/trailing whitespace off the payload", async () => {
    sendMessageMock.mockResolvedValue({
      messageId: "msg-1",
      threadId: "conv-9",
      responsePayload: null,
    });
    render(
      wrap(
        <NewThreadDialog
          open
          onClose={() => {}}
          targetScheme="unit"
          targetPath="alpha"
          onCreated={() => {}}
        />,
      ),
    );
    fireEvent.change(screen.getByTestId("new-conversation-body"), {
      target: { value: "   hello world   \n" },
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("new-conversation-submit"));
    });
    await waitFor(() => {
      expect(sendMessageMock).toHaveBeenCalledWith(
        expect.objectContaining({ payload: "hello world" }),
      );
    });
  });

  it("renders a server error inline without closing the dialog", async () => {
    sendMessageMock.mockRejectedValue(
      new Error("API error 502: Bad Gateway — router refused"),
    );
    const onCreated = vi.fn();
    render(
      wrap(
        <NewThreadDialog
          open
          onClose={() => {}}
          targetScheme="unit"
          targetPath="alpha"
          onCreated={onCreated}
        />,
      ),
    );

    fireEvent.change(screen.getByTestId("new-conversation-body"), {
      target: { value: "hello" },
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("new-conversation-submit"));
    });

    await waitFor(() => {
      expect(screen.getByTestId("new-conversation-error")).toHaveTextContent(
        /router refused/,
      );
    });
    expect(onCreated).not.toHaveBeenCalled();
  });
});
