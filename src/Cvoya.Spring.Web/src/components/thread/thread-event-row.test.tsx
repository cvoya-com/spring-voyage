/**
 * Tests for `ThreadEventRow` body rendering (#1209).
 *
 * The row renders a chat-style bubble per activity event. When the
 * underlying activity event carries a message body — populated by the
 * activity-projection for every `MessageArrived` event — the bubble
 * renders the body text rather than the envelope summary line. Older
 * events without a body fall back to the summary so legacy threads keep
 * rendering correctly.
 */

import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { ThreadEvent } from "@/lib/api/types";

import { ThreadEventRow } from "./thread-event-row";

function makeEvent(overrides: Partial<ThreadEvent> = {}): ThreadEvent {
  return {
    id: "00000000-0000-0000-0000-000000000001",
    timestamp: "2026-04-26T12:00:00Z",
    source: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
    eventType: "MessageArrived",
    severity: "Info",
    summary: "human reply placeholder",
    ...overrides,
  };
}

describe("ThreadEventRow", () => {
  it("renders the body when the MessageArrived event carries one", () => {
    render(
      <ThreadEventRow
        event={makeEvent({ body: "Hello, ada!" })}
      />,
    );

    expect(screen.getByText("Hello, ada!")).toBeTruthy();
    // Falls through summary — but body wins for the visible bubble text.
    expect(screen.queryByText("human reply placeholder")).toBeNull();
  });

  it("falls back to the summary line when no body is present", () => {
    render(
      <ThreadEventRow event={makeEvent()} />,
    );

    expect(screen.getByText("human reply placeholder")).toBeTruthy();
  });

  it("ignores body on non-MessageArrived events", () => {
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

  describe("MessageArrived attribution", () => {
    // The receiving actor projects the event, so event.source is the
    // receiver and event.from is the sender. The bubble must be attributed
    // to the sender, otherwise an agent's reply renders as a human-sent
    // (right-aligned) bubble on the human's timeline.
    it("attributes the bubble to event.from when present", () => {
      const { container } = render(
        <ThreadEventRow
          event={makeEvent({
            // Receiver-projected: human emitted the receive event.
            source: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
            // Underlying sender: the agent.
            from: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
            body: "Hello savas",
          })}
        />,
      );

      const row = container.querySelector("[data-testid^='conversation-event-']");
      expect(row?.getAttribute("data-role")).toBe("agent");
      expect(
        screen.getByTestId("conversation-event-source-name").textContent,
      ).toBe("ada");
    });

    it("falls back to event.source when from is absent", () => {
      const { container } = render(
        <ThreadEventRow
          event={makeEvent({
            source: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
            body: "Hello",
          })}
        />,
      );

      const row = container.querySelector("[data-testid^='conversation-event-']");
      expect(row?.getAttribute("data-role")).toBe("agent");
      expect(
        screen.getByTestId("conversation-event-source-name").textContent,
      ).toBe("ada");
    });

    it("attributes a human-sent message to the human even when the receiver projected it", () => {
      const { container } = render(
        <ThreadEventRow
          event={makeEvent({
            source: { id: "22222222-2222-2222-2222-222222222222", address: "agent://ada", displayName: "ada" },
            from: { id: "11111111-1111-1111-1111-111111111111", address: "human://savas", displayName: "savas" },
            body: "What's up?",
          })}
        />,
      );

      const row = container.querySelector("[data-testid^='conversation-event-']");
      expect(row?.getAttribute("data-role")).toBe("human");
      expect(
        screen.getByTestId("conversation-event-source-name").textContent,
      ).toBe("savas");
    });
  });

  // #1161: dispatch failures must surface inline in the conversation thread
  // with the platform's error styling — operators cannot be expected to
  // open the activity log to discover that a message failed to dispatch.
  describe("error event rendering (#1161)", () => {
    it("renders ErrorOccurred with role=alert and destructive styling", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            eventType: "ErrorOccurred",
            severity: "Error",
            summary: "Dispatch failed: agent did not become ready within 60s",
          })}
        />,
      );

      const alert = screen.getByRole("alert");
      expect(alert).toBeTruthy();
      expect(
        screen.getByText(
          "Dispatch failed: agent did not become ready within 60s",
        ),
      ).toBeTruthy();
    });

    it("renders with data-role=error for ErrorOccurred events", () => {
      const { container } = render(
        <ThreadEventRow
          event={makeEvent({
            eventType: "ErrorOccurred",
            severity: "Error",
            summary: "Dispatch failed",
          })}
        />,
      );

      const row = container.querySelector("[data-role='error']");
      expect(row).not.toBeNull();
    });

    it("renders severity=Error events with the error layout even when eventType is not ErrorOccurred", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            eventType: "StateChanged",
            severity: "Error",
            summary: "State transition error",
          })}
        />,
      );

      expect(screen.getByRole("alert")).toBeTruthy();
      expect(screen.getByText("State transition error")).toBeTruthy();
    });

    it("renders normal MessageArrived events without role=alert", () => {
      render(
        <ThreadEventRow
          event={makeEvent({ body: "Regular message" })}
        />,
      );

      expect(screen.queryByRole("alert")).toBeNull();
    });
  });

  // #2089: body-text address-folding. A weak / noisy LLM may mimic the
  // prompt-format the agent SDK uses for prior turns and emit
  // `[ts] human://<guid>: …` inside its own reply body. The bubble must
  // fold those raw addresses down to display names so platform-internal
  // addressing doesn't leak into the chat UI.
  describe("body-text address folding (#2089)", () => {
    const SAVAS = {
      id: "d6cb6b9d-436f-41d5-9927-f333f309abeb",
      address: "human:d6cb6b9d436f41d59927f333f309abeb",
      displayName: "Savas",
    };
    const ADA = {
      id: "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7",
      address: "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
      displayName: "ada",
    };

    it("renders the resolved display name in place of a raw human:// address", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            body: "human://d6cb6b9d436f41d59927f333f309abeb: hello there",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      expect(screen.getByText("Savas: hello there")).toBeTruthy();
      expect(
        screen.queryByText(/d6cb6b9d436f41d59927f333f309abeb/),
      ).toBeNull();
    });

    it("replaces every address in a body with multiple references", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            body:
              "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7 → " +
              "human://d6cb6b9d436f41d59927f333f309abeb",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      expect(screen.getByText("ada → Savas")).toBeTruthy();
    });

    it("preserves prose around folded addresses", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            body: "[2026-05-10 20:54:39Z] human://d6cb6b9d436f41d59927f333f309abeb: can you check?",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      expect(
        screen.getByText("[2026-05-10 20:54:39Z] Savas: can you check?"),
      ).toBeTruthy();
    });

    it("renders <unknown> when the address can't be resolved against any participant", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            // GUID that's not in the participants list and not the
            // event's own source / from / to.
            body: "ack human://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa done",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      expect(screen.getByText("ack <unknown> done")).toBeTruthy();
    });

    it("resolves the event's own source / from / to even when no thread participants are passed", () => {
      // No `participants` prop — we still want the message's own
      // attribution to fold so a body that mentions the recipient by
      // address renders cleanly. Surfaces that don't pass the full
      // participants list (legacy / inbox) get this for free.
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            from: ADA,
            to: SAVAS,
            body: "human://d6cb6b9d436f41d59927f333f309abeb you there?",
          })}
        />,
      );

      expect(screen.getByText("Savas you there?")).toBeTruthy();
    });

    it("is a no-op when the body contains no address forms", () => {
      render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            body: "Just prose, no addresses.",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      expect(screen.getByText("Just prose, no addresses.")).toBeTruthy();
    });

    // #2128: a body that starts with a JSON object is folded into a
    // collapsed structured-payload card below the bubble. The bubble
    // keeps the prose only (still address-resolved). Detection is
    // shape-only — anything that parses as a top-level object triggers
    // the card; bodies whose leading `{` doesn't parse render unchanged.
    describe("structured-payload split (#2128)", () => {
      it("renders pure prose unchanged when no leading JSON envelope is present", () => {
        // Regression pin against #2130 — a body that's all conversational
        // text must still flow through the address-folding path with no
        // structured-payload card created.
        render(
          <ThreadEventRow
            event={makeEvent({
              source: ADA,
              body: "human://d6cb6b9d436f41d59927f333f309abeb: hello there",
            })}
            participants={[SAVAS, ADA]}
          />,
        );

        expect(screen.getByText("Savas: hello there")).toBeTruthy();
        expect(screen.queryByLabelText("Structured payload")).toBeNull();
      });

      it("renders the prose in the bubble and the JSON envelope as a collapsed card when both are present", () => {
        render(
          <ThreadEventRow
            event={makeEvent({
              source: ADA,
              body:
                '{ "data": { "has_approved_review": true } }\n\n' +
                "[2026-05-10 20:54:39Z] human://d6cb6b9d436f41d59927f333f309abeb: can you check which installation covers this repo?",
            })}
            participants={[SAVAS, ADA]}
          />,
        );

        expect(
          screen.getByText(
            "[2026-05-10 20:54:39Z] Savas: can you check which installation covers this repo?",
          ),
        ).toBeTruthy();

        // Card defaults to collapsed — the label is reachable via its
        // accessible-name (aria-label on the section).
        const card = screen.getByLabelText("Structured payload");
        expect(card).toBeTruthy();

        // The JSON body is not rendered in the collapsed state.
        expect(screen.queryByText(/has_approved_review/)).toBeNull();
      });

      it("renders only the card when the body is a pure JSON object (no trailing prose)", () => {
        const { container } = render(
          <ThreadEventRow
            event={makeEvent({
              source: ADA,
              body: '{ "data": { "has_approved_review": true } }',
            })}
            participants={[SAVAS, ADA]}
          />,
        );

        // No bubble paragraph — the bubble's whole content is prose,
        // and the prose is empty in this case so the bubble is omitted.
        expect(container.querySelector("p.whitespace-pre-wrap")).toBeNull();
        expect(screen.getByLabelText("Structured payload")).toBeTruthy();
      });

      it("renders unchanged when the body looks JSON-y but does not parse", () => {
        // Starts with `{` but isn't valid JSON — falls through to the
        // raw-body render path.
        const malformed = "{not valid json at all }";
        render(
          <ThreadEventRow
            event={makeEvent({ source: ADA, body: malformed })}
            participants={[SAVAS, ADA]}
          />,
        );

        expect(screen.getByText(malformed)).toBeTruthy();
        expect(screen.queryByLabelText("Structured payload")).toBeNull();
      });

      it("expands the card on click and reveals the pretty-printed payload", () => {
        render(
          <ThreadEventRow
            event={makeEvent({
              source: ADA,
              body: '{"data":{"has_approved_review":true}} reply prose',
            })}
            participants={[SAVAS, ADA]}
          />,
        );

        const toggle = screen.getByRole("button", { name: "Structured payload" });
        expect(toggle.getAttribute("aria-expanded")).toBe("false");

        fireEvent.click(toggle);

        expect(toggle.getAttribute("aria-expanded")).toBe("true");
        const body = screen.getByText(/"has_approved_review": true/);
        expect(body).toBeTruthy();
        // Pretty-printed with two-space indentation.
        expect(body.textContent).toContain('"data": {');
      });

      it("exposes the card with an accessible name and a keyboard-reachable toggle", () => {
        render(
          <ThreadEventRow
            event={makeEvent({
              source: ADA,
              body: '{"x":1}',
            })}
            participants={[SAVAS, ADA]}
          />,
        );

        const card = screen.getByLabelText("Structured payload");
        expect(card.tagName).toBe("SECTION");

        const toggle = screen.getByRole("button", { name: "Structured payload" });
        expect(toggle.tagName).toBe("BUTTON");
        // Keyboard-reachable: a real <button> element (Tab-reachable by
        // default; no tabindex hack, no div masquerading as a button)
        // exposing aria-expanded so screen readers announce the state.
        expect(toggle.getAttribute("aria-expanded")).toBe("false");
      });
    });

    it("emits the rendered body as plain text (no aria-label / no link)", () => {
      // Accessibility: the folded text should read the same to a screen
      // reader as it does visually. We rely on the surrounding <p>'s
      // text content; no special aria attribute on the replaced segment.
      const { container } = render(
        <ThreadEventRow
          event={makeEvent({
            source: ADA,
            body: "human://d6cb6b9d436f41d59927f333f309abeb: hello",
          })}
          participants={[SAVAS, ADA]}
        />,
      );

      // No anchor, no chip — just plain text inside the bubble paragraph.
      expect(container.querySelector("a[href*='Savas']")).toBeNull();
      expect(container.querySelector("[aria-label*='Savas']")).toBeNull();
    });
  });
});
