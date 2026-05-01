import { describe, expect, it } from "vitest";

import {
  isHumanAddress,
  parseThreadSource,
  ROLE_STYLES,
  roleFromEvent,
} from "./role";

describe("parseThreadSource", () => {
  it("splits scheme and path for navigation form", () => {
    expect(parseThreadSource("agent://ada")).toEqual({
      scheme: "agent",
      path: "ada",
      kind: "navigation",
      raw: "agent://ada",
    });
  });

  it("preserves nested paths in navigation form", () => {
    expect(parseThreadSource("agent://team/ada")).toEqual({
      scheme: "agent",
      path: "team/ada",
      kind: "navigation",
      raw: "agent://team/ada",
    });
  });

  it("falls back to system scheme when no separator is present", () => {
    expect(parseThreadSource("scheduler")).toEqual({
      scheme: "system",
      path: "scheduler",
      kind: "navigation",
      raw: "scheduler",
    });
  });

  it("lowercases the scheme for navigation form", () => {
    expect(parseThreadSource("HUMAN://savas")).toEqual({
      scheme: "human",
      path: "savas",
      kind: "navigation",
      raw: "HUMAN://savas",
    });
  });

  // Identity form tests (#1490)
  it("parses identity form agent:id:<uuid>", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(parseThreadSource(`agent:id:${uuid}`)).toEqual({
      scheme: "agent",
      path: uuid,
      kind: "identity",
      raw: `agent:id:${uuid}`,
    });
  });

  it("parses identity form unit:id:<uuid>", () => {
    const uuid = "2a3b4c5d-0000-0000-0000-000000000002";
    expect(parseThreadSource(`unit:id:${uuid}`)).toEqual({
      scheme: "unit",
      path: uuid,
      kind: "identity",
      raw: `unit:id:${uuid}`,
    });
  });

  it("lowercases the scheme for identity form", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    const result = parseThreadSource(`AGENT:id:${uuid}`);
    expect(result.scheme).toBe("agent");
    expect(result.kind).toBe("identity");
  });

  it("falls back to navigation parse when ':id:' path contains slashes (not a UUID)", () => {
    // "agent:id:some/path" does not look like an identity form — the path
    // has slashes, so fall through to nav-form parsing.
    const result = parseThreadSource("agent://id:some/path");
    expect(result.kind).toBe("navigation");
    expect(result.scheme).toBe("agent");
  });
});

describe("isHumanAddress", () => {
  it("returns true for human:// navigation form", () => {
    expect(isHumanAddress("human://savas")).toBe(true);
  });

  it("returns false for agent:id: identity form", () => {
    expect(isHumanAddress("agent:id:1f9e3c2d-0000-0000-0000-000000000001")).toBe(false);
  });

  it("returns false for unit:// navigation form", () => {
    expect(isHumanAddress("unit://engineering")).toBe(false);
  });
});

describe("roleFromEvent", () => {
  it("maps navigation-form scheme to role for message events", () => {
    expect(roleFromEvent("human://savas", "MessageReceived")).toBe("human");
    expect(roleFromEvent("agent://ada", "MessageSent")).toBe("agent");
    expect(roleFromEvent("unit://eng", "ConversationStarted")).toBe("unit");
  });

  it("maps identity-form scheme to role for message events (#1490)", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(roleFromEvent(`agent:id:${uuid}`, "MessageReceived")).toBe("agent");
    expect(roleFromEvent(`unit:id:${uuid}`, "MessageReceived")).toBe("unit");
  });

  it("treats DecisionMade as a tool call regardless of source form", () => {
    const uuid = "1f9e3c2d-0000-0000-0000-000000000001";
    expect(roleFromEvent("agent://ada", "DecisionMade")).toBe("tool");
    expect(roleFromEvent(`agent:id:${uuid}`, "DecisionMade")).toBe("tool");
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
