import { describe, expect, it } from "vitest";

import {
  addressOf,
  isHumanAddress,
  looksLikeUuid,
  parseThreadSource,
  participantDisplayName,
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

// #1630
describe("looksLikeUuid", () => {
  it("returns true for dashed UUIDs", () => {
    expect(looksLikeUuid("d4ce4258-ab40-4c10-be06-407cc5ec9139")).toBe(true);
  });

  it("returns true for undashed (32 hex char) UUIDs", () => {
    expect(looksLikeUuid("d4ce4258ab404c10be06407cc5ec9139")).toBe(true);
  });

  it("returns false for plain names", () => {
    expect(looksLikeUuid("ada")).toBe(false);
    expect(looksLikeUuid("engineering-team/ada")).toBe(false);
  });

  it("returns false for empty input", () => {
    expect(looksLikeUuid("")).toBe(false);
  });

  it("returns false for partially UUID-shaped strings", () => {
    expect(looksLikeUuid("d4ce4258-ab40")).toBe(false);
  });
});

// #1630
describe("addressOf", () => {
  it("returns the address from a ParticipantRef object", () => {
    expect(addressOf({ address: "agent://ada", displayName: "ada" })).toBe(
      "agent://ada",
    );
  });

  it("returns plain string addresses unchanged", () => {
    expect(addressOf("agent://ada")).toBe("agent://ada");
  });

  it("returns empty string for null / undefined", () => {
    expect(addressOf(null)).toBe("");
    expect(addressOf(undefined)).toBe("");
  });

  it("returns empty string when address field is missing", () => {
    expect(addressOf({ displayName: "ada" })).toBe("");
  });
});

// #1630 / #1635 — post-#1635 the server guarantees a non-empty
// `displayName` on every ParticipantRef-shaped DTO. The portal therefore
// uses the server-supplied label verbatim and only falls back to the
// legacy heuristic for raw-string inputs (pre-#1502 server shape) or
// missing display names (defensive).
describe("participantDisplayName", () => {
  it("returns the server-supplied displayName when present", () => {
    expect(
      participantDisplayName({
        address: "agent://ada",
        displayName: "Ada Lovelace",
      }),
    ).toBe("Ada Lovelace");
  });

  it("returns the server-supplied displayName even when it looks UUID-shaped", () => {
    // Post-#1635 the server resolves names from the directory; if the
    // operator legitimately named an agent with a UUID-shaped string the
    // portal must pass it through. Filtering UUID-shape is now the
    // server's job (handled via the <deleted> sentinel for missing
    // entities), not the portal's.
    const looksLikeId = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    expect(
      participantDisplayName({
        address: "agent://ada",
        displayName: looksLikeId,
      }),
    ).toBe(looksLikeId);
  });

  it("returns the deleted-sentinel pass-through when the server emits one", () => {
    // The server resolver returns "<deleted>" for entities the
    // directory can no longer resolve (#1635). The portal renders that
    // string directly.
    expect(
      participantDisplayName({
        address: "agent:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7",
        displayName: "<deleted>",
      }),
    ).toBe("<deleted>");
  });

  it("falls back to the address path for navigation-form bare-string addresses", () => {
    // Bare-string inputs come from pre-#1502 servers that didn't ship the
    // ParticipantRef shape. The legacy heuristic still applies.
    expect(participantDisplayName("agent://ada")).toBe("ada");
  });

  it("falls back to the path when a ParticipantRef has empty displayName (defensive)", () => {
    expect(participantDisplayName({ address: "agent://ada" })).toBe("ada");
  });

  it("returns null for legacy human navigation-form addresses (path may be a UUID)", () => {
    expect(participantDisplayName({ address: "human://savas" })).toBeNull();
  });

  it("returns null for legacy identity-form agent addresses (path is a UUID)", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    expect(
      participantDisplayName({ address: `agent:id:${id}` }),
    ).toBeNull();
  });

  it("returns null for legacy identity-form unit addresses (path is a UUID)", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    expect(
      participantDisplayName({ address: `unit:id:${id}` }),
    ).toBeNull();
  });

  it("returns null for a UUID-shaped path on a legacy non-human navigation address", () => {
    const id = "d4ce4258-ab40-4c10-be06-407cc5ec9139";
    expect(
      participantDisplayName({ address: `agent://${id}` }),
    ).toBeNull();
  });

  it("returns null for null / undefined / empty address", () => {
    expect(participantDisplayName(null)).toBeNull();
    expect(participantDisplayName(undefined)).toBeNull();
    expect(participantDisplayName("")).toBeNull();
  });
});
