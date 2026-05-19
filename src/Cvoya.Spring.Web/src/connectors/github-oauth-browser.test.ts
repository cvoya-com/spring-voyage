import { describe, expect, it } from "vitest";

import {
  mintBindingId,
  parseOAuthCallbackPayload,
  parseStoredOAuthCallback,
} from "@connector-github/github-oauth-browser";

describe("parseOAuthCallbackPayload", () => {
  it("extracts the sessionId / login pair from a session-only handoff", () => {
    expect(
      parseOAuthCallbackPayload({
        type: "spring-voyage:github-oauth-session",
        sessionId: "sess-1",
        login: "octocat",
      }),
    ).toEqual({
      sessionId: "sess-1",
      login: "octocat",
      patSecretName: undefined,
      bindingId: undefined,
      error: undefined,
      reason: undefined,
    });
  });

  // ADR-0047 §13: the binding-wizard intent's callback adds
  // `patSecretName` and `bindingId` to the postMessage payload; the
  // bridge must surface both so the wizard wires them into the
  // binding-create call.
  it("extracts patSecretName + bindingId for the binding-wizard intent (ADR-0047 §13)", () => {
    expect(
      parseOAuthCallbackPayload({
        type: "spring-voyage:github-oauth-session",
        sessionId: "sess-2",
        login: "octocat",
        patSecretName: "binding/abcdef0123456789abcdef0123456789/github/pat",
        bindingId: "abcdef0123456789abcdef0123456789",
      }),
    ).toEqual(
      expect.objectContaining({
        sessionId: "sess-2",
        login: "octocat",
        patSecretName: "binding/abcdef0123456789abcdef0123456789/github/pat",
        bindingId: "abcdef0123456789abcdef0123456789",
      }),
    );
  });

  it("returns null for foreign-type messages", () => {
    expect(
      parseOAuthCallbackPayload({
        type: "some-other-channel",
        sessionId: "sess-3",
      }),
    ).toBeNull();
  });

  it("surfaces the error path when GitHub returned a denial", () => {
    expect(
      parseOAuthCallbackPayload({
        type: "spring-voyage:github-oauth-session",
        error: "access_denied",
        reason: "User declined consent.",
      }),
    ).toEqual(
      expect.objectContaining({
        error: "access_denied",
        reason: "User declined consent.",
      }),
    );
  });
});

describe("parseStoredOAuthCallback", () => {
  it("parses a JSON-encoded payload from localStorage", () => {
    const stored = JSON.stringify({
      type: "spring-voyage:github-oauth-session",
      sessionId: "sess-1",
    });
    expect(parseStoredOAuthCallback(stored)).toEqual(
      expect.objectContaining({ sessionId: "sess-1" }),
    );
  });

  it("returns null for non-JSON values", () => {
    expect(parseStoredOAuthCallback("not-json")).toBeNull();
    expect(parseStoredOAuthCallback(null)).toBeNull();
  });
});

describe("mintBindingId", () => {
  it("returns a 32-character no-dash hex string", () => {
    const id = mintBindingId();
    expect(id).toMatch(/^[0-9a-f]{32}$/);
  });
});
