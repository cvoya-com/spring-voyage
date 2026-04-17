import { describe, expect, it } from "vitest";

import {
  parseConversationSource,
  ROLE_STYLES,
  roleFromEvent,
} from "./role";

describe("parseConversationSource", () => {
  it("splits scheme and path", () => {
    expect(parseConversationSource("agent://ada")).toEqual({
      scheme: "agent",
      path: "ada",
      raw: "agent://ada",
    });
  });

  it("preserves nested paths", () => {
    expect(parseConversationSource("agent://team/ada")).toEqual({
      scheme: "agent",
      path: "team/ada",
      raw: "agent://team/ada",
    });
  });

  it("falls back to system scheme when no separator is present", () => {
    expect(parseConversationSource("scheduler")).toEqual({
      scheme: "system",
      path: "scheduler",
      raw: "scheduler",
    });
  });

  it("lowercases the scheme", () => {
    expect(parseConversationSource("HUMAN://savas")).toEqual({
      scheme: "human",
      path: "savas",
      raw: "HUMAN://savas",
    });
  });
});

describe("roleFromEvent", () => {
  it("maps scheme to role for message events", () => {
    expect(roleFromEvent("human://savas", "MessageReceived")).toBe("human");
    expect(roleFromEvent("agent://ada", "MessageSent")).toBe("agent");
    expect(roleFromEvent("unit://eng", "ConversationStarted")).toBe("unit");
  });

  it("treats DecisionMade as a tool call regardless of source", () => {
    expect(roleFromEvent("agent://ada", "DecisionMade")).toBe("tool");
    expect(roleFromEvent("unit://eng", "DecisionMade")).toBe("tool");
  });

  it("falls back to system for unknown schemes", () => {
    expect(roleFromEvent("scheduler", "StateChanged")).toBe("system");
    expect(roleFromEvent("foo://bar", "StateChanged")).toBe("system");
  });
});

describe("ROLE_STYLES", () => {
  it("right-aligns human bubbles and left-aligns the rest", () => {
    expect(ROLE_STYLES.human.align).toBe("end");
    expect(ROLE_STYLES.agent.align).toBe("start");
    expect(ROLE_STYLES.unit.align).toBe("start");
    expect(ROLE_STYLES.tool.align).toBe("start");
    expect(ROLE_STYLES.system.align).toBe("start");
  });
});
