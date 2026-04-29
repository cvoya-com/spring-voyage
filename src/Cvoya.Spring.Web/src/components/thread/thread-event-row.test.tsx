/**
 * Tests for `ThreadEventRow` body rendering (#1209).
 *
 * The row renders a chat-style bubble per activity event. When the
 * underlying activity event carries a message body — populated by the
 * activity-projection for every `MessageReceived` event — the bubble
 * renders the body text rather than the envelope summary line. Older
 * events without a body fall back to the summary so legacy threads keep
 * rendering correctly.
 */

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { ThreadEvent } from "@/lib/api/types";

import { ThreadEventRow } from "./thread-event-row";

function makeEvent(overrides: Partial<ThreadEvent> = {}): ThreadEvent {
  return {
    id: "00000000-0000-0000-0000-000000000001",
    timestamp: "2026-04-26T12:00:00Z",
    source: "agent://ada",
    eventType: "MessageReceived",
    severity: "Info",
    summary: "Received Domain message X from human://savas",
    ...overrides,
  };
}

describe("ThreadEventRow", () => {
  it("renders the body when the MessageReceived event carries one", () => {
    render(
      <ThreadEventRow
        event={makeEvent({ body: "Hello, ada!" })}
      />,
    );

    expect(screen.getByText("Hello, ada!")).toBeTruthy();
    // Falls through summary — but body wins for the visible bubble text.
    expect(screen.queryByText("Received Domain message X from human://savas"))
      .toBeNull();
  });

  it("falls back to the summary line when no body is present", () => {
    render(
      <ThreadEventRow event={makeEvent()} />,
    );

    expect(
      screen.getByText("Received Domain message X from human://savas"),
    ).toBeTruthy();
  });

  it("ignores body on non-MessageReceived events", () => {
    render(
      <ThreadEventRow
        event={makeEvent({
          eventType: "ConversationStarted",
          summary: "Started conv",
          body: "leaked body",
        })}
      />,
    );

    expect(screen.getByText("Started conv")).toBeTruthy();
    expect(screen.queryByText("leaked body")).toBeNull();
  });
});
